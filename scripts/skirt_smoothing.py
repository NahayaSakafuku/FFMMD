"""Deterministic quaternion filtering and capsule/box contact guards.

This module is pure Python: no Blender or physics-engine source is imported.
Quaternions use x, y, z, w, matching the FFMMD cache. Distances use the reference
rig's Blender units and are proxy measurements, not a target-mesh guarantee.
"""
import math


def add(a, b): return tuple(x + y for x, y in zip(a, b))
def sub(a, b): return tuple(x - y for x, y in zip(a, b))
def scale(a, s): return tuple(x * s for x in a)
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def length(a): return math.sqrt(dot(a, a))
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def normalize(q):
    if len(q) != 4 or not all(math.isfinite(x) for x in q):
        raise ValueError('Quaternion must contain four finite values')
    n = length(q)
    if n < 1e-12:
        raise ValueError('Quaternion has zero length')
    return scale(q, 1 / n)


def conjugate(q): return (-q[0], -q[1], -q[2], q[3])


def multiply(a, b):
    av, bv = a[:3], b[:3]
    v = add(add(scale(bv, a[3]), scale(av, b[3])), cross(av, bv))
    return (*v, a[3]*b[3]-dot(av, bv))


def rotate(q, v):
    # q is already normalized at public input / filter boundaries.
    uv = cross(q[:3], v)
    return add(v, add(scale(uv, 2*q[3]), scale(cross(q[:3], uv), 2)))


def rotation_vector(q):
    q = normalize(q)
    if q[3] < 0:
        q = scale(q, -1)
    n = length(q[:3])
    if n < 1e-10:
        return scale(q[:3], 2)
    return scale(q[:3], 2*math.atan2(n, max(0, q[3]))/n)


def from_rotation_vector(v):
    a = length(v)
    if a < 1e-10:
        return normalize((*scale(v, .5), 1.0))
    return (*scale(v, math.sin(a/2)/a), math.cos(a/2))


def angular_distance(a, b):
    return 2*math.acos(min(1.0, abs(dot(normalize(a), normalize(b)))))


def slerp(a, b, t):
    a, b = normalize(a), normalize(b)
    if dot(a, b) < 0:
        b = scale(b, -1)
    delta = rotation_vector(multiply(conjugate(a), b))
    return normalize(multiply(a, from_rotation_vector(scale(delta, t))))


def smooth_track(track, window=2, strength=.65, maximum_degrees=3.0,
                 angular_bandwidth_degrees=12.0):
    """Symmetric short-window quaternion average, bounded per original sample.

    Large pose changes receive little weight through the angular bilateral term.
    Endpoints stay fixed. The result has no playback-history dependence or lag.
    The caller must run the contact guard before accepting these candidates.
    """
    if not 0 <= window <= 8 or not 0 <= strength <= 1 or not 0 <= maximum_degrees <= 15:
        raise ValueError('Invalid smoothing settings')
    if not 0 < angular_bandwidth_degrees <= 180:
        raise ValueError('Invalid angular bandwidth')
    if not track:
        return []
    raw = [normalize(q) for q in track]
    for i in range(1, len(raw)):
        if dot(raw[i-1], raw[i]) < 0:
            raw[i] = scale(raw[i], -1)
    if window == 0 or strength == 0 or maximum_degrees == 0:
        return raw
    cap = math.radians(maximum_degrees)
    bandwidth = math.radians(angular_bandwidth_degrees)
    result = []
    for i, center in enumerate(raw):
        if i == 0 or i == len(raw)-1:
            result.append(center)
            continue
        mean, total = (0.0, 0.0, 0.0), 0.0
        for j in range(max(0, i-window), min(len(raw), i+window+1)):
            delta = rotation_vector(multiply(conjugate(center), raw[j]))
            # Binomial-like temporal weights, with angular edge preservation.
            weight = math.exp(-.5*((j-i)/max(.5, window*.65))**2 - .5*(length(delta)/bandwidth)**2)
            mean = add(mean, scale(delta, weight))
            total += weight
        delta = scale(mean, strength/total)
        n = length(delta)
        if n > cap:
            delta = scale(delta, cap/n)
        result.append(normalize(multiply(center, from_rotation_vector(delta))))
    return result


def segment_aabb_signed_distance(a, b, half):
    """Minimum signed point-to-AABB distance along a closed segment.

    Outside the box, the piecewise quadratic minimizer is exact. Inside, the
    deepest signed distance is the maximum of the concave minimum face gap;
    candidates are segment endpoints and all intersecting affine face gaps.
    This is a continuous penetration proxy for a capsule's axis, not Bullet's
    exact contact-manifold penetration depth.
    """
    d = sub(b, a)
    breaks = {0.0, 1.0}
    for i in range(3):
        if abs(d[i]) > 1e-14:
            for face in (-half[i], half[i]):
                t = (face-a[i])/d[i]
                if 0 < t < 1:
                    breaks.add(t)
    intervals = sorted(breaks)
    best = float('inf')
    inside = False
    for low, high in zip(intervals, intervals[1:]):
        midpoint = (low+high)/2
        p = add(a, scale(d, midpoint))
        active = [(i, half[i] if p[i] > half[i] else -half[i])
                  for i in range(3) if abs(p[i]) > half[i]]
        if not active:
            inside = True
            continue
        denominator = sum(d[i]*d[i] for i, _ in active)
        t = low if denominator < 1e-28 else max(low, min(high, -sum(d[i]*(a[i]-face) for i, face in active)/denominator))
        point = add(a, scale(d, t))
        best = min(best, sum(max(abs(point[i])-half[i], 0)**2 for i in range(3)))
    if inside or any(all(abs(p[i]) <= half[i] for i in range(3)) for p in (a, b)):
        faces = [(half[i]-a[i], -d[i]) for i in range(3)] + [(half[i]+a[i], d[i]) for i in range(3)]
        candidates = {0.0, 1.0}
        for i, (intercept, slope) in enumerate(faces):
            for other_intercept, other_slope in faces[i+1:]:
                if abs(slope-other_slope) > 1e-14:
                    t = (other_intercept-intercept)/(slope-other_slope)
                    if 0 <= t <= 1:
                        candidates.add(t)
        deepest = max(min(c+s*t for c, s in faces) for t in candidates)
        if deepest >= 0:
            return -deepest
    # Zero-length segment and numerical boundary cases.
    for p in (a, b):
        best = min(best, sum(max(abs(p[i])-half[i], 0)**2 for i in range(3)))
    return math.sqrt(best)


def capsule_box_penetration(capsule, box):
    """Signed-distance proxy overlap of sphere/capsule versus oriented box.

    capsule=(endpoint_a, endpoint_b, radius), box=(center, quaternion, half_size).
    Positive result means overlap. A fast conservative bounding-sphere test
    skips only pairs that are certainly separated.
    """
    a, b, radius = capsule
    center, q, half = box
    ab = sub(b, a)
    denominator = dot(ab, ab)
    t = 0 if denominator < 1e-20 else max(0, min(1, dot(sub(center, a), ab)/denominator))
    if length(sub(center, add(a, scale(ab, t)))) > radius+length(half):
        return 0.0
    inverse = conjugate(q)
    local_a, local_b = rotate(inverse, sub(a, center)), rotate(inverse, sub(b, center))
    return max(0.0, radius-segment_aabb_signed_distance(local_a, local_b, half))


def guarded_frame(raw, candidate, measure, max_backoffs=8, tolerance=1e-7):
    """Accept one common frame blend only if every proxy overlap is no worse.

    A shared blend amount preserves the original inter-panel physics coupling.
    If contact checks reject all candidates, this frame returns exactly raw.
    """
    original = tuple(measure(raw))
    if any(not math.isfinite(x) or x < 0 for x in original):
        raise ValueError('Invalid original contact measurements')
    for step in range(max_backoffs+1):
        amount = 2.0**-step
        mixed = [slerp(a, b, amount) for a, b in zip(raw, candidate)]
        current = tuple(measure(mixed))
        if len(current) != len(original) or any(not math.isfinite(x) for x in current):
            raise ValueError('Invalid candidate contact measurements')
        if all(after <= before+tolerance for before, after in zip(original, current)):
            return mixed, amount, original, current
    return list(raw), 0.0, original, original


def track_metrics(tracks):
    count, speed2, acceleration2, maximum_step = 0, 0.0, 0.0, 0.0
    acceleration_count = 0
    for track in tracks:
        previous_velocity = None
        for a, b in zip(track, track[1:]):
            velocity = rotation_vector(multiply(conjugate(a), b))
            speed2 += dot(velocity, velocity)
            maximum_step = max(maximum_step, length(velocity))
            count += 1
            if previous_velocity is not None:
                # Body-local angular velocities; useful as a temporal roughness
                # metric, not physical acceleration in a common world frame.
                delta = sub(velocity, previous_velocity)
                acceleration2 += dot(delta, delta)
                acceleration_count += 1
            previous_velocity = velocity
    return {'RootMeanSquareStepDegrees':math.degrees(math.sqrt(speed2/max(1, count))),
            'RootMeanSquareAngularChangeDegrees':math.degrees(math.sqrt(acceleration2/max(1, acceleration_count))),
            'MaximumStepDegrees':math.degrees(maximum_step)}
