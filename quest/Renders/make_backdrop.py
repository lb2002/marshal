"""Builds Assets/Lotse/Editor/RenderBackdrop/desk.png from a Quest passthrough capture (both eyes side by side).

Takes the left-eye half, paints out the old app overlay that was on screen when the capture was taken (four agent
spheres with labels and the placement banner), and removes most of the lens pre-distortion with a single radial
term. Usage (Python 3 with numpy and opencv-python):

    python make_backdrop.py <capture.png> <desk.png>
"""
import sys

import cv2
import numpy as np

OUT_W, OUT_H = 1600, 1000
K = 0.25        # radial term: source radius = r / (1 + K r^2)
ZOOM = 1.3      # how much of the eye image the output spans (1 = half the shorter side at the output's half width)


def paint_out_overlay(left):
    # positions measured on a 688x736 preview of the left half, so x3 here
    s = left.shape[1] / 688.0
    mask = np.zeros(left.shape[:2], np.uint8)
    for cx, cy in [(318, 398), (422, 340), (530, 298), (640, 280)]:
        cv2.circle(mask, (int(cx * s), int(cy * s)), int(34 * s), 255, -1)
        cv2.rectangle(mask, (int((cx - 30) * s), int((cy + 22) * s)), (int((cx + 30) * s), int((cy + 46) * s)), 255, -1)
    poly = (np.array([(352, 316), (628, 219), (636, 264), (360, 356)], np.float32) * s).astype(np.int32)
    cv2.fillPoly(mask, [poly], 255)
    mask = cv2.dilate(mask, np.ones((9, 9), np.uint8))
    small = cv2.resize(left, None, fx=0.5, fy=0.5, interpolation=cv2.INTER_AREA)
    fill = cv2.inpaint(small, cv2.resize(mask, None, fx=0.5, fy=0.5, interpolation=cv2.INTER_NEAREST), 12, cv2.INPAINT_TELEA)
    fill = cv2.GaussianBlur(cv2.resize(fill, (left.shape[1], left.shape[0]), interpolation=cv2.INTER_CUBIC), (0, 0), 6)
    m = (cv2.GaussianBlur(mask, (0, 0), 6).astype(np.float32) / 255.0)[..., None]
    return (left * (1 - m) + fill * m).astype(np.uint8)


def undistort(left):
    h, w = left.shape[:2]
    ys, xs = np.mgrid[0:OUT_H, 0:OUT_W].astype(np.float32)
    nx = (xs - OUT_W / 2) / (OUT_W / 2) * ZOOM
    ny = (ys - OUT_H / 2) / (OUT_W / 2) * ZOOM
    f = 1.0 / (1.0 + K * (nx * nx + ny * ny))
    r = min(w, h) / 2
    map_x = (w / 2 + nx * f * r).astype(np.float32)
    map_y = (h / 2 + ny * f * r).astype(np.float32)
    return cv2.remap(left, map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


def main():
    src = cv2.imread(sys.argv[1], cv2.IMREAD_COLOR)
    left = src[:, : src.shape[1] // 2].copy()
    cv2.imwrite(sys.argv[2], undistort(paint_out_overlay(left)))


if __name__ == "__main__":
    main()
