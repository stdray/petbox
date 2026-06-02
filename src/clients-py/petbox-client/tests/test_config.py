"""Tests for ResolvedConfig — ported from the TS SDK's config.test.ts."""

from petbox_client import ResolvedConfig


class TestGet:
    def test_flat_template_traverses_dotted_path(self) -> None:
        cfg = ResolvedConfig({"db": {"host": "localhost"}}, None)
        assert cfg.get("db.host") == "localhost"

    def test_flat_template_missing_path_returns_none(self) -> None:
        cfg = ResolvedConfig({"db": {"host": "localhost"}}, None)
        assert cfg.get("db.missing") is None

    def test_flat_template_traversing_into_non_object_returns_none(self) -> None:
        cfg = ResolvedConfig({"db": "string-value"}, None)
        assert cfg.get("db.host") is None

    def test_dotnet_template_direct_key_lookup(self) -> None:
        cfg = ResolvedConfig({"Db:Host": "localhost"}, None)
        assert cfg.get("Db:Host") == "localhost"

    def test_coerces_non_string_scalars(self) -> None:
        cfg = ResolvedConfig({"port": 8080, "enabled": True}, None)
        assert cfg.get("port") == "8080"
        assert cfg.get("enabled") == "true"

    def test_none_value_returns_none(self) -> None:
        cfg = ResolvedConfig({"x": None}, None)
        assert cfg.get("x") is None


class TestGetNumber:
    def test_parses_json_number(self) -> None:
        assert ResolvedConfig({"port": 8080}, None).get_number("port") == 8080

    def test_parses_numeric_string(self) -> None:
        assert ResolvedConfig({"port": "8080"}, None).get_number("port") == 8080

    def test_non_numeric_string_returns_none(self) -> None:
        assert ResolvedConfig({"port": "abc"}, None).get_number("port") is None

    def test_missing_path_returns_none(self) -> None:
        assert ResolvedConfig({}, None).get_number("port") is None

    def test_bool_is_not_a_number(self) -> None:
        assert ResolvedConfig({"x": True}, None).get_number("x") is None


class TestGetBool:
    def test_parses_json_boolean(self) -> None:
        assert ResolvedConfig({"enabled": True}, None).get_bool("enabled") is True

    def test_parses_true_false_strings_case_insensitively(self) -> None:
        cfg = ResolvedConfig({"a": "true", "b": "FALSE", "c": "True"}, None)
        assert cfg.get_bool("a") is True
        assert cfg.get_bool("b") is False
        assert cfg.get_bool("c") is True

    def test_non_boolean_string_returns_none(self) -> None:
        assert ResolvedConfig({"x": "yes"}, None).get_bool("x") is None


class TestToEnv:
    def test_flattens_nested_objects_to_dotted_keys(self) -> None:
        cfg = ResolvedConfig({"db": {"host": "localhost", "port": 5432}}, None)
        env = cfg.to_env()
        assert env["db.host"] == "localhost"
        assert env["db.port"] == "5432"

    def test_non_object_data_returns_empty(self) -> None:
        assert ResolvedConfig(None, None).to_env() == {}
        assert ResolvedConfig("scalar", None).to_env() == {}


class TestMetadata:
    def test_etag_is_preserved(self) -> None:
        assert ResolvedConfig({}, "abc123").etag == "abc123"

    def test_raw_data_is_preserved(self) -> None:
        raw = {"a": 1}
        assert ResolvedConfig(raw, None).data is raw
