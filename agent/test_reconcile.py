"""Unit tests for the pure reconcile decision (plan_actions). No Docker required."""
import unittest

from petbox_deploy_agent import (
    ACTUAL_RUNNING,
    ACTUAL_STOPPED,
    DESIRED_RUNNING,
    DESIRED_STOPPED,
    plan_actions,
)


def item(service, confighash, desired=DESIRED_RUNNING, image="img1", project="proj"):
    return {
        "service": service,
        "imageDigest": image,
        "desired": desired,
        "configHash": confighash,
        "project": project,
        "env": {},
    }


def actual(state=ACTUAL_RUNNING, confighash="h1"):
    return {"container_id": "c1", "state": state, "confighash": confighash, "image": "img1"}


class PlanActionsTests(unittest.TestCase):
    def test_runs_when_absent(self):
        actions = plan_actions([item("bot", "h1")], {})
        self.assertEqual(actions, [{"action": "run", "service": "bot", "item": item("bot", "h1")}])

    def test_noop_when_running_with_same_confighash(self):
        actions = plan_actions([item("bot", "h1")], {"bot": actual(ACTUAL_RUNNING, "h1")})
        self.assertEqual(actions, [])

    def test_recreates_when_confighash_changed(self):
        actions = plan_actions([item("bot", "h2")], {"bot": actual(ACTUAL_RUNNING, "h1")})
        self.assertEqual([a["action"] for a in actions], ["run"])

    def test_runs_when_present_but_not_running(self):
        actions = plan_actions([item("bot", "h1")], {"bot": actual(ACTUAL_STOPPED, "h1")})
        self.assertEqual([a["action"] for a in actions], ["run"])

    def test_removes_when_desired_stopped(self):
        actions = plan_actions([item("bot", "h1", desired=DESIRED_STOPPED)], {"bot": actual()})
        self.assertEqual(actions, [{"action": "remove", "service": "bot", "item": None}])

    def test_noop_when_desired_stopped_and_absent(self):
        self.assertEqual(plan_actions([item("bot", "h1", desired=DESIRED_STOPPED)], {}), [])

    def test_self_fences_unassigned_managed_container(self):
        # 'old' runs on this node but is no longer in desired (e.g. relocated away) → removed.
        actions = plan_actions([item("bot", "h1")], {"bot": actual(), "old": actual()})
        self.assertIn({"action": "remove", "service": "old", "item": None}, actions)
        self.assertNotIn("bot", [a["service"] for a in actions if a["action"] == "remove"])


if __name__ == "__main__":
    unittest.main()
