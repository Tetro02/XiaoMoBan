package io.localpen.tablet;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.drawable.Drawable;

final class TabletIcon extends Drawable {
    enum Shape { PEN, MOUSE, ERASER, CROP, SETTINGS, WIFI, USB, BLUETOOTH, STOP, CHECK }

    private final Shape shape;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();

    TabletIcon(Shape shape, int color) {
        this.shape = shape;
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(1.65f);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
    }

    @Override public void draw(Canvas canvas) {
        canvas.save();
        canvas.translate(getBounds().left, getBounds().top);
        canvas.scale(getBounds().width() / 24f, getBounds().height() / 24f);
        switch (shape) {
            case PEN:
                line(canvas, 4, 20, 5.5f, 14, 16.5f, 3, 21, 7.5f, 10, 18.5f, 4, 20);
                line(canvas, 13.5f, 6, 18, 10.5f);
                break;
            case MOUSE:
                canvas.drawRoundRect(6, 3, 18, 21, 5, 5, paint);
                line(canvas, 12, 6, 12, 10);
                break;
            case ERASER:
                line(canvas, 8, 20, 3, 15, 13.5f, 4.5f, 21, 12, 13, 20, 8, 20);
                line(canvas, 7.5f, 10.5f, 15.5f, 18.5f);
                line(canvas, 13, 20, 21, 20);
                break;
            case CROP:
                line(canvas, 3, 8, 3, 3, 8, 3);
                line(canvas, 16, 3, 21, 3, 21, 8);
                line(canvas, 21, 16, 21, 21, 16, 21);
                line(canvas, 8, 21, 3, 21, 3, 16);
                canvas.drawRect(8, 8, 16, 16, paint);
                break;
            case SETTINGS:
                slider(canvas, 5, 8);
                slider(canvas, 12, 15);
                slider(canvas, 19, 10);
                break;
            case WIFI:
                canvas.drawArc(1, 3, 23, 23, 224, 92, false, paint);
                canvas.drawArc(5, 8, 19, 21, 225, 90, false, paint);
                canvas.drawArc(9, 13, 15, 19, 225, 90, false, paint);
                canvas.drawPoint(12, 19.5f, paint);
                break;
            case USB:
                line(canvas, 12, 19, 12, 3, 9, 6);
                line(canvas, 12, 3, 15, 6);
                line(canvas, 12, 15, 7, 12, 7, 9);
                line(canvas, 12, 13, 17, 10, 17, 7);
                canvas.drawCircle(12, 20, 1.5f, paint);
                break;
            case BLUETOOTH:
                line(canvas, 7, 7, 17, 17, 12, 22, 12, 2, 17, 7, 7, 17);
                break;
            case STOP:
                canvas.drawRoundRect(6, 6, 18, 18, 2, 2, paint);
                break;
            case CHECK:
                line(canvas, 4, 12, 9.5f, 17.5f, 20, 7);
                break;
        }
        canvas.restore();
    }

    private void slider(Canvas canvas, float y, float position) {
        line(canvas, 3, y, position - 2.2f, y);
        canvas.drawCircle(position, y, 2, paint);
        line(canvas, position + 2.2f, y, 21, y);
    }

    private void line(Canvas canvas, float... coordinates) {
        path.reset();
        path.moveTo(coordinates[0], coordinates[1]);
        for (int i = 2; i < coordinates.length; i += 2) path.lineTo(coordinates[i], coordinates[i + 1]);
        canvas.drawPath(path, paint);
    }

    @Override public void setAlpha(int alpha) { paint.setAlpha(alpha); }
    @Override public void setColorFilter(ColorFilter filter) { paint.setColorFilter(filter); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
