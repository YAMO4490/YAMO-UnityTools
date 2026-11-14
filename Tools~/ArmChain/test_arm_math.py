import unittest
from arm_math import Poses,make_targets,stabilize_poles,check_tpose,qangle


def fixture(frames=12):
    p=Poses(frames)
    for f in range(frames):
        for i in range(8):p.set_quat(f,i,(0,0,0,1))
        for b,sign in [(0,1),(4,-1)]:
            p.set_pos(f,b+1,(0,0,0));p.set_pos(f,b+2,(sign,.002 if f<6 else -.002,0));p.set_pos(f,b+3,(sign*2,0,0))
    return p


class HandGoalTests(unittest.TestCase):
    def test_shoulders_anchor_proportional_hand_goal_and_full_world_rotation(self):
        raw=fixture(2);body=fixture(2);r0=fixture(1);t0=fixture(1)
        for b in (0,4):
            body.set_pos(0,b+2,(2,0,0));body.set_pos(0,b+3,(4,0,0))
            t0.set_pos(0,b+2,(2,0,0));t0.set_pos(0,b+3,(4,0,0))
            raw.set_pos(1,b+1,(5,0,0));raw.set_pos(1,b+2,(6,1,0));raw.set_pos(1,b+3,(5,2,0))
            body.set_pos(1,b+1,(11,3,0));raw.set_quat(1,b+3,(0,0,.3826834324,.9238795325))
        target,scales=make_targets(raw,body,r0,t0)
        self.assertAlmostEqual(scales[0],2,places=5)
        self.assertAlmostEqual(target.pos(1,3)[0],11,places=5)
        self.assertAlmostEqual(target.pos(1,3)[1],7,places=4)
        self.assertLess(qangle(target.quat(1,3),(0,0,.3826834324,.9238795325)),.001)

    def test_ambiguous_elbow_plane_changes_without_moving_hands(self):
        p=fixture();out,report=stabilize_poles(p)
        self.assertTrue(report['windows']);self.assertTrue(report['handGoalsUnchanged'])
        self.assertNotEqual(p.pos(6,2),out.pos(6,2))
        for f in range(p.frames):
            for b in (3,7):self.assertEqual(p.bone(f,b),out.bone(f,b))

    def test_large_source_discontinuity_is_kept(self):
        p=fixture();p.set_pos(7,2,(0,1,0));p.set_pos(7,3,(1,0,0))
        out,report=stabilize_poles(p)
        self.assertTrue(report['skippedSourceDiscontinuities'])
        self.assertEqual(out.pos(7,2),p.pos(7,2))

    def test_bad_raw_reference_is_rejected(self):
        p=fixture(1);p.set_pos(0,2,(0,1,0));p.set_pos(0,3,(1,1,0))
        with self.assertRaisesRegex(ValueError,'T-pose'):check_tpose(p)


if __name__=='__main__':unittest.main()
