use std::collections::VecDeque;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use pennote_bridge_receiver::{ClientInfo, InputMode, PointerFrame};

const MAX_TAP: Duration = Duration::from_millis(270);
const TAP_GAP: Duration = Duration::from_millis(430);
const BARREL_GRACE: Duration = Duration::from_millis(105);
const ACTIVE_TAP_ARRIVAL_LIMIT: Duration = Duration::from_millis(650);
const TAP_MOVEMENT: f32 = 0.025;
const SERIES_MOVEMENT: f32 = 0.09;

#[derive(Default)]
pub(crate) struct BarrelGestures {
    state: Mutex<State>,
}

impl BarrelGestures {
    pub(crate) fn note(&self, client: &ClientInfo, frame: &PointerFrame) {
        self.state.lock().unwrap().note(&client.connection_id, frame, Instant::now());
    }

    pub(crate) fn disconnect(&self, client: &ClientInfo) {
        let mut state = self.state.lock().unwrap();
        if state.client.as_deref() == Some(&client.connection_id) {
            *state = State::default();
        }
    }

    pub(crate) fn reset(&self) {
        *self.state.lock().unwrap() = State::default();
    }

    pub(crate) fn take(&self) -> u32 {
        let mut state = self.state.lock().unwrap();
        state.expire(Instant::now());
        state.pending.pop_front().unwrap_or(0)
    }
}

#[derive(Default)]
struct State {
    client: Option<String>,
    mode: Option<InputMode>,
    contact: bool,
    tap: Option<Tap>,
    series: Option<Series>,
    last_barrel: Option<SampleTime>,
    barrel_released: Option<SampleTime>,
    pending: VecDeque<u32>,
}

struct Tap {
    start: SampleTime,
    origin: (f32, f32),
    held: bool,
}

struct Series {
    count: u32,
    origin: (f32, f32),
    last_up: SampleTime,
}

#[derive(Clone, Copy)]
struct SampleTime {
    received: Instant,
    source_us: u64,
}

impl SampleTime {
    fn since(self, earlier: Self) -> Duration {
        if earlier.source_us != 0 && self.source_us >= earlier.source_us {
            Duration::from_micros(self.source_us - earlier.source_us)
        } else {
            self.received.saturating_duration_since(earlier.received)
        }
    }
}

impl State {
    fn note(&mut self, client: &str, frame: &PointerFrame, now: Instant) {
        if self.client.as_deref() != Some(client) || self.mode != Some(frame.mode) {
            self.client = Some(client.to_owned());
            self.mode = Some(frame.mode);
            self.contact = false;
            self.tap = None;
            self.series = None;
            self.last_barrel = None;
            self.barrel_released = None;
        }
        let time = SampleTime { received: now, source_us: frame.client_time_us };
        self.expire_at(time, MAX_TAP);
        let (held, contact, in_range) = match frame.mode {
            InputMode::Pen => (frame.barrel, frame.contact, frame.in_range),
            InputMode::RelativeMouse => (frame.right_button, frame.left_button, frame.in_range),
            InputMode::AbsoluteMouse => (frame.right_button, frame.left_button, true),
        };
        let was_contact = self.contact;
        self.contact = contact;
        let point = (frame.x, frame.y);

        if held {
            self.last_barrel = Some(time);
            self.barrel_released = None;
        }
        if !in_range {
            // Some hover exits are reported between otherwise valid taps. An exit alone
            // is never a successful lift, but completed taps can still join the next tap.
            self.tap = None;
            return;
        }
        if contact && !was_contact {
            let near_handoff = self.last_barrel.is_some_and(|at| time.since(at) < BARREL_GRACE);
            if !held && !near_handoff {
                self.finish_series();
                return;
            }
            if self.series.as_ref().is_some_and(|series| moved(series.origin, point, SERIES_MOVEMENT)) {
                self.finish_series();
            }
            self.tap = Some(Tap { start: time, origin: point, held });
        } else if let Some(tap) = self.tap.as_mut() {
            // One UI may clear barrel in the first or last contact frame.
            tap.held |= held;
            if moved(tap.origin, point, TAP_MOVEMENT) {
                self.tap = None;
                self.series = None;
            } else if !contact {
                let tap = self.tap.take().unwrap();
                if tap.held {
                    let series = self.series.get_or_insert(Series { count: 0, origin: tap.origin, last_up: time });
                    series.count += 1;
                    series.last_up = time;
                    if series.count == 3 {
                        self.finish_series();
                    }
                } else {
                    self.finish_series();
                }
            }
        }
        if !contact && !was_contact && !held && self.series.is_some() {
            // A lift with missing barrel flags is ambiguous. An unheld hover
            // is the first useful indication that the side key was released.
            self.barrel_released.get_or_insert(time);
        }
    }

    fn expire(&mut self, now: Instant) {
        self.expire_at(SampleTime { received: now, source_us: 0 }, ACTIVE_TAP_ARRIVAL_LIMIT);
    }

    fn expire_at(&mut self, now: SampleTime, tap_limit: Duration) {
        if self.tap.as_ref().is_some_and(|tap| now.since(tap.start) > tap_limit) {
            self.tap = None;
            self.series = None;
        }
        let released = self.barrel_released.is_some_and(|at| now.since(at) >= BARREL_GRACE);
        if self.tap.is_none() && self.series.as_ref().is_some_and(|series| released || now.since(series.last_up) >= TAP_GAP) {
            self.finish_series();
        }
    }

    fn finish_series(&mut self) {
        self.barrel_released = None;
        if let Some(series) = self.series.take().filter(|series| series.count >= 2) {
            if self.pending.len() == 8 {
                self.pending.pop_front();
            }
            self.pending.push_back(series.count);
        }
    }
}

fn moved(origin: (f32, f32), point: (f32, f32), limit: f32) -> bool {
    let (dx, dy) = (point.0 - origin.0, point.1 - origin.1);
    dx * dx + dy * dy > limit * limit
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(contact: bool, barrel: bool, x: f32) -> PointerFrame {
        PointerFrame {
            mode: InputMode::Pen, x, y: 0.5, pressure: if contact { 0.5 } else { 0.0 },
            tilt_x: 0, tilt_y: 0, rotation: 0, in_range: true, contact, barrel,
            inverted: false, eraser: false, left_button: false, right_button: false,
            middle_button: false, client_time_us: 0, relative_gain: 1.0, hover_pan_deadzone_px: None,
        }
    }

    fn tap(state: &mut State, start: Instant) {
        state.note("tablet", &frame(true, true, 0.5), start);
        state.note("tablet", &frame(false, true, 0.5), start + Duration::from_millis(50));
    }

    #[test]
    fn third_tap_takes_precedence_over_double_tap() {
        let start = Instant::now();
        let mut state = State::default();
        tap(&mut state, start);
        tap(&mut state, start + Duration::from_millis(125));
        state.expire(start + Duration::from_millis(300));
        assert!(state.pending.is_empty());
        tap(&mut state, start + Duration::from_millis(350));
        state.expire(start + Duration::from_secs(1));
        assert_eq!(state.pending, [3]);
    }

    #[test]
    fn double_tap_fires_once_on_timeout_or_barrel_release() {
        let start = Instant::now();
        let mut state = State::default();
        tap(&mut state, start);
        tap(&mut state, start + Duration::from_millis(100));
        state.expire(start + Duration::from_millis(579));
        assert!(state.pending.is_empty());
        state.expire(start + Duration::from_millis(580));
        tap(&mut state, start + Duration::from_millis(600));
        tap(&mut state, start + Duration::from_millis(700));
        state.note("tablet", &frame(false, false, 0.5), start + Duration::from_millis(780));
        state.expire(start + Duration::from_secs(2));
        assert_eq!(state.pending, [2, 2]);
    }

    #[test]
    fn dragging_long_contacts_and_pen_exit_do_not_count() {
        let start = Instant::now();
        let mut state = State::default();
        tap(&mut state, start);
        state.note("tablet", &frame(true, true, 0.5), start + Duration::from_millis(100));
        state.note("tablet", &frame(true, true, 0.55), start + Duration::from_millis(110));
        state.note("tablet", &frame(false, true, 0.5), start + Duration::from_millis(150));
        tap(&mut state, start + Duration::from_millis(200));
        state.note("tablet", &frame(true, true, 0.5), start + Duration::from_millis(300));
        state.note("tablet", &frame(false, true, 0.5), start + Duration::from_millis(580));
        tap(&mut state, start + Duration::from_millis(620));
        state.note("tablet", &frame(true, true, 0.5), start + Duration::from_millis(720));
        let exit = PointerFrame { in_range: false, ..frame(false, false, 0.5) };
        state.note("tablet", &exit, start + Duration::from_millis(740));
        state.expire(start + Duration::from_secs(2));
        assert!(state.pending.is_empty());
    }

    #[test]
    fn samsung_up_without_barrel_and_brief_hover_exit_keep_the_series() {
        let start = Instant::now();
        let mut state = State::default();
        let input = |state: &mut State, contact, held, in_range, ms| {
            let frame = PointerFrame { in_range, ..frame(contact, held, 0.5) };
            state.note("tablet", &frame, start + Duration::from_millis(ms));
        };
        input(&mut state, true, true, true, 0);
        input(&mut state, false, false, true, 70);
        input(&mut state, false, false, false, 100);
        input(&mut state, false, true, true, 125);
        input(&mut state, true, false, true, 150);
        input(&mut state, true, true, true, 155);
        input(&mut state, false, false, true, 200);
        input(&mut state, true, true, true, 510);
        input(&mut state, false, false, true, 550);
        assert_eq!(state.pending, [3]);
    }

    #[test]
    fn released_barrel_does_not_turn_ordinary_taps_into_gestures() {
        let start = Instant::now();
        let mut state = State::default();
        state.note("tablet", &frame(false, true, 0.5), start);
        for tick in [20, 200] {
            state.note("tablet", &frame(true, false, 0.5), start + Duration::from_millis(tick));
            state.note("tablet", &frame(false, false, 0.5), start + Duration::from_millis(tick + 50));
        }
        state.expire(start + Duration::from_secs(1));
        assert!(state.pending.is_empty());
    }

    #[test]
    fn tap_duration_uses_tablet_timestamps_when_network_delivery_stalls() {
        let start = Instant::now();
        let mut state = State::default();
        let input = |state: &mut State, contact, source, arrival| {
            let frame = PointerFrame { client_time_us: source, ..frame(contact, true, 0.5) };
            state.note("tablet", &frame, start + Duration::from_millis(arrival));
        };
        input(&mut state, true, 1_000_000, 0);
        state.expire(start + Duration::from_millis(350));
        input(&mut state, false, 1_050_000, 410);
        input(&mut state, true, 1_130_000, 415);
        input(&mut state, false, 1_180_000, 420);
        state.expire(start + Duration::from_millis(900));
        assert_eq!(state.pending, [2]);
    }
}
