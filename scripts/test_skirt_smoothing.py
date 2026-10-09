"""Run with python -m unittest discover -s scripts -p test_skirt_smoothing.py."""
import math
import unittest
from skirt_smoothing import (angular_distance, capsule_box_penetration, dot,
    from_rotation_vector, guarded_frame, normalize, rotate, segment_aabb_signed_distance,
    smooth_track, track_metrics)


def q(degrees): return from_rotation_vector((0, 0, math.radians(degrees)))


class QuaternionFilteringTests(unittest.TestCase):
    def test_antipodal_samples_preserve_constant_rotation(self):
        original=q(43)
        result=smooth_track([original,tuple(-v for v in original),original])
        self.assertTrue(all(angular_distance(original,v)<1e-7 for v in result))

    def test_reduces_small_jitter_with_bounded_normalized_correction(self):
        raw=[q(20+(-1)**i*2) for i in range(31)]
        filtered=smooth_track(raw,maximum_degrees=1.2)
        self.assertLess(track_metrics([filtered])['RootMeanSquareAngularChangeDegrees'],
                        track_metrics([raw])['RootMeanSquareAngularChangeDegrees'])
        self.assertTrue(all(angular_distance(a,b)<=math.radians(1.2)+1e-10 for a,b in zip(raw,filtered)))
        self.assertTrue(all(abs(dot(v,v)-1)<1e-10 for v in filtered))
        self.assertEqual(filtered[0],normalize(raw[0])); self.assertEqual(filtered[-1],normalize(raw[-1]))

    def test_filter_is_symmetric_and_has_no_directional_phase_shift(self):
        raw=[q(v) for v in [0,1,1.4,4,3.1,3,5,6,7]]
        forward=smooth_track(raw)
        reverse=list(reversed(smooth_track(list(reversed(raw)))))
        self.assertTrue(all(angular_distance(a,b)<1e-7 for a,b in zip(forward,reverse)))

    def test_guard_rejects_worsened_pair_even_if_total_improves(self):
        raw=[q(0)];candidate=[q(20)]
        def measure(values):
            angle=angular_distance(raw[0],values[0])
            return (.1-angle,.1+angle)
        result,blend,before,after=guarded_frame(raw,candidate,measure,tolerance=0)
        self.assertEqual(blend,0);self.assertEqual(result,raw);self.assertEqual(before,after)

    def test_guard_backoff_and_replay_are_deterministic(self):
        raw=[q(0),q(5)];candidate=[q(4),q(9)]
        def measure(values):
            angle=angular_distance(raw[0],values[0])
            return (max(0,angle-math.radians(1.5)),)
        a=guarded_frame(raw,candidate,measure,tolerance=0)
        b=guarded_frame(raw,candidate,measure,tolerance=0)
        self.assertEqual(a,b);self.assertEqual(a[1],.25)
        self.assertAlmostEqual(angular_distance(raw[1],a[0][1]),math.radians(1))


class CollisionProxyTests(unittest.TestCase):
    def test_parallel_segment_clearance_and_endpoint_minimum(self):
        self.assertAlmostEqual(segment_aabb_signed_distance((-2,2,0),(2,2,0),(1,1,1)),1)
        self.assertAlmostEqual(segment_aabb_signed_distance((2,2,0),(3,3,0),(1,1,1)),math.sqrt(2))

    def test_segment_through_box_and_zero_length_are_signed(self):
        self.assertAlmostEqual(segment_aabb_signed_distance((-2,0,0),(2,0,0),(1,1,1)),-1)
        self.assertAlmostEqual(segment_aabb_signed_distance((0,.5,0),(0,.5,0),(1,1,1)),-.5)
        self.assertAlmostEqual(segment_aabb_signed_distance((2,0,0),(2,0,0),(1,1,1)),1)

    def test_inside_depth_detects_worsening_that_unsigned_distance_misses(self):
        box=((0,0,0),q(0),(1,1,1))
        shallow=capsule_box_penetration(((.8,0,0),(.8,0,0),.1),box)
        deep=capsule_box_penetration(((0,0,0),(0,0,0),.1),box)
        self.assertAlmostEqual(shallow,.3);self.assertAlmostEqual(deep,1.1)

    def test_rotated_box_is_equivalent_and_far_broadphase_is_safe(self):
        half=(1,.2,.3);rotation=q(60);center=(3,4,0)
        a,b=(0,.5,0),(1,.5,0)
        world=lambda p:tuple(x+y for x,y in zip(rotate(rotation,p),center))
        first=capsule_box_penetration((a,b,.4),((0,0,0),q(0),half))
        second=capsule_box_penetration((world(a),world(b),.4),(center,rotation,half))
        self.assertAlmostEqual(first,second)
        self.assertEqual(capsule_box_penetration(((30,30,30),(30,30,35),.1),(center,rotation,half)),0)

    def test_contact_guard_on_actual_geometry(self):
        raw=[q(0)];candidate=[q(10)]
        capsule=((1.25,-1,0),(1.25,1,0),.2)
        measure=lambda values:[capsule_box_penetration(capsule,((0,0,0),values[0],(1,.2,.2)))]
        result,blend,before,after=guarded_frame(raw,candidate,measure,tolerance=0)
        self.assertTrue(all(a<=b for a,b in zip(after,before)))
        self.assertTrue(0<=blend<=1)


if __name__=='__main__': unittest.main()
