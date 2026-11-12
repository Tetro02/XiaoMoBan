package io.localpen.tablet;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.view.Gravity;
import android.widget.Button;
import android.widget.TextView;

final class TabletStyle {
    static final int BACKGROUND = 0xff111824;
    static final int CHROME = 0xff182131;
    static final int PANEL = 0xff212e40;
    static final int BOARD = 0xff1b2637;
    static final int BOARD_EDIT = 0xff202f45;
    static final int GRID = 0xff3a4b63;
    static final int LINE = 0xff34445b;
    static final int TEXT = 0xfff0f3fa;
    static final int MUTED = 0xffb3c0d2;
    static final int ACCENT = 0xffb1caff;
    static final int INK = 0xff182840;
    static final int CONNECTED = 0xffa6e2c8;
    static final int ERASER_COLOR = 0xffffc49f;

    private TabletStyle() {}

    static int dp(Context context, float value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    static TextView label(Context context, String text, int size, int color) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setIncludeFontPadding(false);
        return view;
    }

    static void medium(TextView view) {
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
    }

    static GradientDrawable rounded(Context context, int color, int radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(context, radius));
        return drawable;
    }

    static Button tool(Context context, String text, TabletIcon.Shape shape, Runnable action) {
        Button button = new Button(context, null, android.R.attr.borderlessButtonStyle);
        button.setAllCaps(false);
        button.setTextSize(13);
        button.setSingleLine(true);
        button.setGravity(Gravity.CENTER);
        button.setIncludeFontPadding(false);
        button.setMinWidth(dp(context, 48));
        button.setMinimumWidth(dp(context, 48));
        button.setMinHeight(dp(context, 48));
        button.setMinimumHeight(dp(context, 48));
        button.setPadding(dp(context, 10), 0, dp(context, 10), 0);
        button.setCompoundDrawablePadding(dp(context, 5));
        medium(button);
        button.setOnClickListener(ignored -> action.run());
        setTool(button, text, shape, false);
        return button;
    }

    static void setTool(Button button, String text, TabletIcon.Shape shape, boolean selected) {
        Context context = button.getContext();
        int foreground = selected ? INK : TEXT;
        button.setText(text);
        button.setSelected(selected);
        button.setTextColor(foreground);
        if (shape == null) {
            button.setCompoundDrawablesRelative(null, null, null, null);
        } else {
            TabletIcon icon = new TabletIcon(shape, foreground);
            int size = dp(context, 17);
            icon.setBounds(0, 0, size, size);
            button.setCompoundDrawablesRelative(icon, null, null, null);
        }
        StateListDrawable background = new StateListDrawable();
        GradientDrawable focus = rounded(context, selected ? ACCENT : PANEL, 10);
        focus.setStroke(dp(context, 1), selected ? INK : ACCENT);
        background.addState(new int[] {android.R.attr.state_focused}, focus);
        background.addState(new int[] {android.R.attr.state_hovered},
                rounded(context, selected ? ACCENT : PANEL, 10));
        background.addState(new int[0], rounded(context, selected ? ACCENT : 0x00000000, 10));
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(selected ? 0x240f2448 : 0x29b1caff),
                background, rounded(context, 0xffffffff, 10)));
    }
}
