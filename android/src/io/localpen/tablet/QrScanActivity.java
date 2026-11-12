package io.localpen.tablet;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.ImageFormat;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.widget.Toast;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.ReaderException;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;

import java.io.IOException;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

@SuppressWarnings("deprecation")
public final class QrScanActivity extends Activity implements SurfaceHolder.Callback {
    public static final String EXTRA_QR_TEXT = "qr_text";
    private static final int CAMERA_PERMISSION = 1;
    private static final long SCAN_INTERVAL_MS = 140;

    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private SurfaceView preview;
    private Camera camera;
    private boolean resumed;
    private boolean permissionRequested;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setResult(RESULT_CANCELED);
        FrameLayout page = new FrameLayout(this);
        page.setBackgroundColor(TabletStyle.BACKGROUND);
        preview = new SurfaceView(this);
        preview.getHolder().addCallback(this);
        page.addView(preview, new FrameLayout.LayoutParams(-1, -1));

        TextView instruction = TabletStyle.label(this, "将电脑上的配对二维码置于画面内", 16, TabletStyle.TEXT);
        instruction.setGravity(Gravity.CENTER);
        instruction.setPadding(dp(16), dp(10), dp(16), dp(10));
        instruction.setBackground(TabletStyle.rounded(this, TabletStyle.CHROME, 12));
        FrameLayout.LayoutParams instructionPosition = new FrameLayout.LayoutParams(-2, -2,
                Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        instructionPosition.topMargin = dp(14);
        page.addView(instruction, instructionPosition);

        Button cancel = TabletStyle.tool(this, "取消", null, this::finish);
        cancel.setBackground(TabletStyle.rounded(this, TabletStyle.CHROME, 12));
        FrameLayout.LayoutParams cancelPosition = new FrameLayout.LayoutParams(-2, -2,
                Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        cancelPosition.bottomMargin = dp(14);
        page.addView(cancel, cancelPosition);
        setContentView(page);
    }

    @Override protected void onResume() {
        super.onResume();
        resumed = true;
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startCamera();
        } else if (!permissionRequested) {
            permissionRequested = true;
            requestPermissions(new String[] {Manifest.permission.CAMERA}, CAMERA_PERMISSION);
        }
    }

    @Override public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode != CAMERA_PERMISSION) return;
        if (results.length != 0 && results[0] == PackageManager.PERMISSION_GRANTED) startCamera();
        else fail("允许相机权限后才能扫描配对码");
    }

    @Override protected void onPause() {
        resumed = false;
        stopCamera();
        super.onPause();
    }

    @Override protected void onDestroy() {
        decoder.shutdownNow();
        super.onDestroy();
    }

    @Override public void surfaceCreated(SurfaceHolder holder) { startCamera(); }

    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        startCamera();
    }

    @Override public void surfaceDestroyed(SurfaceHolder holder) { stopCamera(); }

    private void startCamera() {
        SurfaceHolder holder = preview.getHolder();
        if (!resumed || isFinishing() || camera != null || !holder.getSurface().isValid()
                || checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        try {
            Camera.CameraInfo info = new Camera.CameraInfo();
            int count = Camera.getNumberOfCameras();
            if (count == 0) {
                fail("这台设备没有可用相机");
                return;
            }
            int cameraId = 0;
            for (int i = 0; i < count; i++) {
                Camera.getCameraInfo(i, info);
                if (info.facing == Camera.CameraInfo.CAMERA_FACING_BACK) {
                    cameraId = i;
                    break;
                }
            }
            Camera.getCameraInfo(cameraId, info);
            Camera opened = Camera.open(cameraId);
            camera = opened;
            int orientation = previewOrientation(info);
            opened.setDisplayOrientation(orientation);
            Camera.Parameters parameters = opened.getParameters();
            parameters.setPreviewFormat(ImageFormat.NV21);
            Camera.Size size = choosePreviewSize(parameters.getSupportedPreviewSizes(), orientation);
            if (size != null) parameters.setPreviewSize(size.width, size.height);
            List<String> focus = parameters.getSupportedFocusModes();
            if (focus != null && focus.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) {
                parameters.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            }
            opened.setParameters(parameters);
            opened.setPreviewDisplay(holder);
            opened.setErrorCallback((error, source) -> {
                if (camera == source) fail("相机连接中断，请重试");
            });
            opened.startPreview();
            Camera.Size frame = opened.getParameters().getPreviewSize();
            nextFrame(opened, frame.width, frame.height);
        } catch (RuntimeException | IOException error) {
            fail("无法打开相机，请确认相机可用后重试");
        }
    }

    private Camera.Size choosePreviewSize(List<Camera.Size> sizes, int orientation) {
        if (sizes == null) return null;
        double ratio = (double) Math.max(1, preview.getWidth()) / Math.max(1, preview.getHeight());
        if (orientation == 90 || orientation == 270) ratio = 1 / ratio;
        Camera.Size best = null;
        double bestScore = Double.POSITIVE_INFINITY;
        for (Camera.Size size : sizes) {
            double score = Math.abs((double) size.width / size.height - ratio);
            if (size.width > 1280 || size.height > 960) score += 2 + (size.width / 10000.0);
            if (size.width < 640 || size.height < 360) score += 1;
            if (score < bestScore) {
                best = size;
                bestScore = score;
            }
        }
        return best;
    }

    private int previewOrientation(Camera.CameraInfo info) {
        int rotation = getWindowManager().getDefaultDisplay().getRotation();
        int degrees = rotation == Surface.ROTATION_90 ? 90 : rotation == Surface.ROTATION_180 ? 180
                : rotation == Surface.ROTATION_270 ? 270 : 0;
        if (info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT) {
            return (360 - (info.orientation + degrees) % 360) % 360;
        }
        return (info.orientation - degrees + 360) % 360;
    }

    private void nextFrame(Camera expected, int width, int height) {
        if (!resumed || camera != expected || isFinishing()) return;
        try {
            expected.setOneShotPreviewCallback((frame, source) -> {
                if (!resumed || camera != source || isFinishing()) return;
                decoder.execute(() -> {
                    String text = decodeFrame(frame, width, height);
                    runOnUiThread(() -> {
                        if (!resumed || camera != source || isFinishing()) return;
                        if (text == null) {
                            preview.postDelayed(() -> nextFrame(source, width, height), SCAN_INTERVAL_MS);
                        } else {
                            setResult(RESULT_OK, new Intent().putExtra(EXTRA_QR_TEXT, text));
                            finish();
                        }
                    });
                });
            });
        } catch (RuntimeException error) {
            fail("无法读取相机预览，请重试");
        }
    }

    static String decodeFrame(byte[] frame, int width, int height) {
        if (frame == null || width <= 0 || height <= 0 || frame.length < (long) width * height) return null;
        PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(frame, width, height,
                0, 0, width, height, false);
        try {
            return new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source))).getText();
        } catch (ReaderException ignored) {
            return null;
        }
    }

    private void stopCamera() {
        Camera opened = camera;
        camera = null;
        if (opened == null) return;
        try {
            opened.stopPreview();
        } catch (RuntimeException ignored) {
            // The preview may already have stopped after a camera error.
        } finally {
            opened.release();
        }
    }

    private void fail(String message) {
        stopCamera();
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
        finish();
    }

    private int dp(float value) { return TabletStyle.dp(this, value); }
}
