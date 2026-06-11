"""Unit tests for the pure site-route planning and heartbeat error reporting."""
import unittest

from petbox_deploy_agent import (
    ACTUAL_MISSING,
    ACTUAL_RUNNING,
    DESIRED_RUNNING,
    DESIRED_STOPPED,
    build_heartbeat,
    plan_caddy,
    plan_site_errors,
    render_caddy_route,
)


def site_item(service, domain, port, desired=DESIRED_RUNNING):
    return {"service": service, "desired": desired,
            "runSpec": {"site": {"domain": domain, "port": port}}}


def bot_item(service, desired=DESIRED_RUNNING):
    return {"service": service, "desired": desired, "runSpec": {}}


class PlanCaddyTests(unittest.TestCase):
    def test_renders_domain_to_loopback_port(self):
        self.assertEqual(render_caddy_route("app.example.com", 8080),
                         "app.example.com {\n\treverse_proxy 127.0.0.1:8080\n}\n")

    def test_writes_new_route(self):
        writes, removes = plan_caddy([site_item("web", "app.example.com", 8080)], {})
        self.assertEqual(writes, {"web.caddy": render_caddy_route("app.example.com", 8080)})
        self.assertEqual(removes, [])

    def test_noop_when_route_matches(self):
        current = {"web.caddy": render_caddy_route("app.example.com", 8080)}
        writes, removes = plan_caddy([site_item("web", "app.example.com", 8080)], current)
        self.assertEqual((writes, removes), ({}, []))

    def test_rewrites_changed_route(self):
        current = {"web.caddy": render_caddy_route("app.example.com", 8080)}
        writes, _ = plan_caddy([site_item("web", "app.example.com", 9090)], current)
        self.assertIn("web.caddy", writes)

    def test_removes_route_of_stopped_or_gone_site(self):
        current = {"web.caddy": render_caddy_route("app.example.com", 8080),
                   "old.caddy": render_caddy_route("old.example.com", 8081)}
        writes, removes = plan_caddy([site_item("web", "app.example.com", 8080, desired=DESIRED_STOPPED)], current)
        self.assertEqual(writes, {})
        self.assertEqual(sorted(removes), ["old.caddy", "web.caddy"])

    def test_bots_get_no_route(self):
        writes, removes = plan_caddy([bot_item("bot")], {})
        self.assertEqual((writes, removes), ({}, []))


class SiteErrorsTests(unittest.TestCase):
    def test_no_errors_when_caddy_present(self):
        self.assertEqual(plan_site_errors([site_item("web", "a.b.c", 80)], caddy_ok=True), {})

    def test_running_site_without_caddy_is_explicit_error(self):
        errors = plan_site_errors([site_item("web", "a.b.c", 80), bot_item("bot")], caddy_ok=False)
        self.assertIn("web", errors)
        self.assertNotIn("bot", errors)
        self.assertIn("caddy is not available", errors["web"])

    def test_stopped_site_without_caddy_is_fine(self):
        errors = plan_site_errors([site_item("web", "a.b.c", 80, desired=DESIRED_STOPPED)], caddy_ok=False)
        self.assertEqual(errors, {})


class HeartbeatErrorsTests(unittest.TestCase):
    def test_error_marks_running_container_unhealthy(self):
        actual = {"web": {"container_id": "c1", "state": ACTUAL_RUNNING, "confighash": "h", "image": "i"}}
        hb = build_heartbeat(actual, {"web": "site route not applied"}, ["docker"])
        self.assertEqual(hb["capabilities"], ["docker"])
        report = hb["actual"][0]
        self.assertFalse(report["healthy"])
        self.assertEqual(report["error"], "site route not applied")

    def test_errored_absent_service_reported_missing(self):
        hb = build_heartbeat({}, {"web": "run failed: boom"})
        self.assertEqual(hb["actual"], [{
            "service": "web", "containerId": None, "state": ACTUAL_MISSING,
            "imageDigest": None, "healthy": False, "error": "run failed: boom",
        }])

    def test_clean_heartbeat_has_no_errors(self):
        actual = {"bot": {"container_id": "c1", "state": ACTUAL_RUNNING, "confighash": "h", "image": "i"}}
        hb = build_heartbeat(actual, {}, ["docker", "caddy"])
        self.assertTrue(hb["actual"][0]["healthy"])
        self.assertIsNone(hb["actual"][0]["error"])


if __name__ == "__main__":
    unittest.main()
