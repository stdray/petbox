"""Unit tests for the pure host-report parsers (sshd security posture, meminfo)."""
import unittest

from petbox_deploy_agent import parse_meminfo, parse_sshd_security


class SshdSecurityTests(unittest.TestCase):
    def test_hardened_host_reports_all_secure(self):
        out = "permitrootlogin no\npasswordauthentication no\nport 22\n"
        self.assertEqual(parse_sshd_security(out),
                         {"rootLoginEnabled": False, "passwordAuthEnabled": False})

    def test_root_enabled_and_password_allowed(self):
        out = "permitrootlogin yes\npasswordauthentication yes\n"
        self.assertEqual(parse_sshd_security(out),
                         {"rootLoginEnabled": True, "passwordAuthEnabled": True})

    def test_prohibit_password_root_still_counts_as_not_disabled(self):
        # owner's wording is literal: root login NOT disabled = warning, key-only included
        out = "permitrootlogin prohibit-password\npasswordauthentication no\n"
        self.assertEqual(parse_sshd_security(out)["rootLoginEnabled"], True)

    def test_unreadable_output_reports_nothing(self):
        self.assertEqual(parse_sshd_security(""), {})


class MeminfoTests(unittest.TestCase):
    def test_parses_total_and_available(self):
        text = "MemTotal:        986524 kB\nMemFree:          81492 kB\nMemAvailable:    102400 kB\n"
        self.assertEqual(parse_meminfo(text), {"totalMb": 963, "availableMb": 100})

    def test_missing_fields_are_absent(self):
        self.assertEqual(parse_meminfo("SwapTotal: 0 kB\n"), {})


if __name__ == "__main__":
    unittest.main()
