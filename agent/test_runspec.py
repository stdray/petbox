"""Unit tests for the pure runSpec -> docker-run-flags mapping. No Docker required."""
import unittest

from petbox_deploy_agent import runspec_args, runspec_command


class RunspecArgsTests(unittest.TestCase):
    def test_empty_spec_defaults_restart_only(self):
        self.assertEqual(runspec_args(None), ["--restart", "unless-stopped"])
        self.assertEqual(runspec_args({}), ["--restart", "unless-stopped"])

    def test_ports_and_volumes(self):
        args = runspec_args({
            "ports": ["127.0.0.1:8080:8080", "5000:5000/udp"],
            "volumes": ["/opt/app/logs:/app/logs", "/opt/app/keys:/app/keys:ro"],
        })
        self.assertEqual(args, [
            "-p", "127.0.0.1:8080:8080",
            "-p", "5000:5000/udp",
            "-v", "/opt/app/logs:/app/logs",
            "-v", "/opt/app/keys:/app/keys:ro",
            "--restart", "unless-stopped",
        ])

    def test_explicit_restart_policy(self):
        self.assertEqual(runspec_args({"restart": "always"}), ["--restart", "always"])

    def test_healthcheck_full_and_partial(self):
        full = runspec_args({"healthcheck": {
            "cmd": "curl -f http://localhost:8080/health",
            "interval": "30s", "timeout": "5s", "retries": 3,
        }})
        self.assertEqual(full, [
            "--restart", "unless-stopped",
            "--health-cmd", "curl -f http://localhost:8080/health",
            "--health-interval", "30s",
            "--health-timeout", "5s",
            "--health-retries", "3",
        ])
        # cmd-only healthcheck emits no interval/timeout/retries flags
        partial = runspec_args({"healthcheck": {"cmd": "true"}})
        self.assertEqual(partial, ["--restart", "unless-stopped", "--health-cmd", "true"])
        # a healthcheck without cmd is ignored entirely
        self.assertEqual(runspec_args({"healthcheck": {"interval": "30s"}}),
                         ["--restart", "unless-stopped"])

    def test_resources_network_labels(self):
        args = runspec_args({
            "resources": {"memory": "256m", "cpus": 0.5},
            "network": "bridge",
            "labels": {"team": "infra", "tier": ""},
        })
        self.assertEqual(args, [
            "--restart", "unless-stopped",
            "--memory", "256m",
            "--cpus", "0.5",
            "--network", "bridge",
            "--label", "team=infra",
            "--label", "tier=",
        ])

    def test_command_override(self):
        self.assertEqual(runspec_command({"command": ["python", "-m", "bot"]}),
                         ["python", "-m", "bot"])
        self.assertEqual(runspec_command({}), [])
        self.assertEqual(runspec_command(None), [])


if __name__ == "__main__":
    unittest.main()
