package com.wander.blink;

import android.graphics.Bitmap;
import android.os.SystemClock;

import com.google.mlkit.vision.common.InputImage;
import com.google.mlkit.vision.face.Face;
import com.google.mlkit.vision.face.FaceDetection;
import com.google.mlkit.vision.face.FaceDetector;
import com.google.mlkit.vision.face.FaceDetectorOptions;

import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

public class MLKitBlink {
    private static FaceDetector detector;
    private static Bitmap bitmap;
    private static int bitmapW = -1, bitmapH = -1;
    private static final AtomicBoolean busy = new AtomicBoolean(false);

    private static float closeThresh = 0.25f;
    private static float openThresh  = 0.60f;
    private static long  minIntervalMs = 250;

    private static boolean closed = false;
    private static long lastBlinkMs = 0;
    private static volatile boolean blinkPending = false;

    public static void init() {
        if (detector != null) return;

        FaceDetectorOptions opts = new FaceDetectorOptions.Builder()
                .setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_FAST)
                .setClassificationMode(FaceDetectorOptions.CLASSIFICATION_MODE_ALL) // "eyes open"
                .enableTracking()
                .build();

        detector = FaceDetection.getClient(opts);
    }

    public static void release() {
        try {
            if (detector != null) detector.close();
        } catch (Exception ignored) {}
        detector = null;

        bitmap = null;
        bitmapW = bitmapH = -1;

        busy.set(false);
        blinkPending = false;
        closed = false;
    }

    public static void setThresholds(float closeT, float openT, int minIntervalMillis) {
        closeThresh = closeT;
        openThresh = openT;
        minIntervalMs = minIntervalMillis;
    }

    // argb: each int is 0xAARRGGBB
    public static void process(int[] argb, int width, int height, int rotationDegrees) {
        if (detector == null) init();
        if (detector == null) return;
        if (argb == null || argb.length < width * height) return;

        if (!busy.compareAndSet(false, true)) return; // skip if still processing

        try {
            if (bitmap == null || bitmapW != width || bitmapH != height) {
                bitmapW = width;
                bitmapH = height;
                bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
            }

            bitmap.setPixels(argb, 0, width, 0, 0, width, height);
            InputImage image = InputImage.fromBitmap(bitmap, rotationDegrees);

            detector.process(image)
                    .addOnSuccessListener(MLKitBlink::handleFaces)
                    .addOnFailureListener(e -> busy.set(false));
        } catch (Exception e) {
            busy.set(false);
        }
    }

    private static void handleFaces(List<Face> faces) {
        try {
            if (faces == null || faces.isEmpty()) return;

            // Pick largest face
            Face best = faces.get(0);
            int bestArea = best.getBoundingBox().width() * best.getBoundingBox().height();
            for (int i = 1; i < faces.size(); i++) {
                Face f = faces.get(i);
                int area = f.getBoundingBox().width() * f.getBoundingBox().height();
                if (area > bestArea) {
                    best = f;
                    bestArea = area;
                }
            }

            float l = best.getLeftEyeOpenProbability();
            float r = best.getRightEyeOpenProbability();

            // If probabilities aren’t computed, they can be -1
            if (l < 0f && r < 0f) return;

            float eye = (l < 0f) ? r : (r < 0f) ? l : Math.min(l, r);
            long now = SystemClock.uptimeMillis();

            if (!closed) {
                if (eye <= closeThresh) closed = true;
            } else {
                if (eye >= openThresh) {
                    if (now - lastBlinkMs >= minIntervalMs) {
                        blinkPending = true;
                        lastBlinkMs = now;
                    }
                    closed = false;
                }
            }
        } finally {
            busy.set(false);
        }
    }

    public static boolean consumeBlink() {
        if (blinkPending) {
            blinkPending = false;
            return true;
        }
        return false;
    }
}
