"""Tests for Azure Communication Services authentication behavior."""

from __future__ import annotations

import os
from pathlib import Path
from types import SimpleNamespace

import pytest

# Imported at module scope so module initialization always happens before a test
# mutates the environment. This keeps every case order- and process-independent.
from src.acs import email_service as email_mod
from src.acs import sms_service as sms_mod

REPO_ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture
def stub_credential(monkeypatch):
    """Replace Call Automation credential construction with a stable test value."""
    credential = SimpleNamespace(name="stub-credential")
    monkeypatch.setattr("src.acs.acs_helper.get_credential", lambda: credential)
    return credential


def test_acs_caller_entra_auth_uses_endpoint_when_connection_string_exists(
    monkeypatch, stub_credential
):
    """Explicit Entra auth takes precedence over a local connection string."""
    captured = {}

    class StubCallAutomationClient:
        def __init__(self, endpoint=None, credential=None):
            captured["mode"] = "credential"
            captured["endpoint"] = endpoint
            captured["credential"] = credential

        @classmethod
        def from_connection_string(cls, connection_string):
            captured["mode"] = "connection_string"
            return cls()

    monkeypatch.setattr("src.acs.acs_helper.CallAutomationClient", StubCallAutomationClient)

    from src.acs.acs_helper import AcsCaller

    AcsCaller(
        source_number="+15555550100",
        callback_url="https://example.test/api/cb",
        acs_connection_string="endpoint=https://x.communication.azure.com/;accesskey=key",
        acs_endpoint="https://x.communication.azure.com/",
        acs_auth_mode="entra",
    )

    assert captured == {
        "mode": "credential",
        "endpoint": "https://x.communication.azure.com/",
        "credential": stub_credential,
    }


def test_acs_caller_auto_uses_connection_string(monkeypatch, stub_credential):
    """Auto mode preserves local connection-string authentication."""
    captured = {}

    class StubCallAutomationClient:
        @classmethod
        def from_connection_string(cls, connection_string):
            captured["connection_string"] = connection_string
            return cls.__new__(cls)

    monkeypatch.setattr("src.acs.acs_helper.CallAutomationClient", StubCallAutomationClient)

    from src.acs.acs_helper import AcsCaller

    caller = AcsCaller(
        source_number="+15555550100",
        callback_url="https://example.test/api/cb",
        acs_connection_string="endpoint=https://x.communication.azure.com/;accesskey=key",
        acs_endpoint="https://x.communication.azure.com/",
        acs_auth_mode="auto",
    )

    assert caller.effective_auth_mode == "connection_string"
    assert captured["connection_string"].endswith("accesskey=key")


def test_acs_caller_auto_uses_entra_when_only_endpoint_is_available(monkeypatch, stub_credential):
    captured = {}

    class StubCallAutomationClient:
        def __init__(self, endpoint=None, credential=None):
            captured["endpoint"] = endpoint
            captured["credential"] = credential

    monkeypatch.setattr("src.acs.acs_helper.CallAutomationClient", StubCallAutomationClient)

    from src.acs.acs_helper import AcsCaller

    caller = AcsCaller(
        source_number="+15555550100",
        callback_url="https://example.test/api/cb",
        acs_endpoint="https://x.communication.azure.com/",
        acs_auth_mode="auto",
    )

    assert caller.effective_auth_mode == "entra"
    assert captured["endpoint"] == "https://x.communication.azure.com/"
    assert captured["credential"] is stub_credential


def _clear_service_auth_environment(monkeypatch, *, sender_variable: str):
    for name in (
        "ACS_AUTH_MODE",
        "ACS_CONNECTION_STRING",
        "ACS_ENDPOINT",
        "AZURE_COMMUNICATION_EMAIL_CONNECTION_STRING",
        "AZURE_COMMUNICATION_SMS_CONNECTION_STRING",
        sender_variable,
    ):
        monkeypatch.delenv(name, raising=False)


def test_email_service_entra_auth_takes_precedence(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.setenv("ACS_AUTH_MODE", "entra")
    monkeypatch.setenv("ACS_ENDPOINT", "https://x.communication.azure.com/")
    monkeypatch.setenv("AZURE_EMAIL_SENDER_ADDRESS", "noreply@example.com")
    monkeypatch.setenv(
        "AZURE_COMMUNICATION_EMAIL_CONNECTION_STRING",
        "endpoint=https://x.communication.azure.com/;accesskey=key",
    )
    captured = {}
    credential = SimpleNamespace(name="stub-credential")

    class StubEmailClient:
        def __init__(self, endpoint, supplied_credential):
            captured["endpoint"] = endpoint
            captured["credential"] = supplied_credential

        @classmethod
        def from_connection_string(cls, connection_string):
            captured["connection_string"] = connection_string
            return cls.__new__(cls)

    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)
    monkeypatch.setattr(email_mod, "EmailClient", StubEmailClient)
    monkeypatch.setattr(email_mod, "get_credential", lambda: credential)

    service = email_mod.EmailService()

    assert service.effective_auth_mode == "entra"
    assert captured == {
        "endpoint": "https://x.communication.azure.com/",
        "credential": credential,
    }
    assert service.is_configured() is True


def test_email_service_auto_prefers_connection_string(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.setenv("ACS_AUTH_MODE", "auto")
    monkeypatch.setenv("ACS_ENDPOINT", "https://x.communication.azure.com/")
    monkeypatch.setenv("AZURE_EMAIL_SENDER_ADDRESS", "noreply@example.com")
    monkeypatch.setenv("ACS_CONNECTION_STRING", "endpoint=https://x;accesskey=key")
    captured = {}

    class StubEmailClient:
        @classmethod
        def from_connection_string(cls, connection_string):
            captured["connection_string"] = connection_string
            return cls.__new__(cls)

    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)
    monkeypatch.setattr(email_mod, "EmailClient", StubEmailClient)

    service = email_mod.EmailService()

    assert service.effective_auth_mode == "connection_string"
    assert captured["connection_string"].endswith("accesskey=key")
    assert service.is_configured() is True


def test_email_service_entra_requires_endpoint(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.setenv("ACS_AUTH_MODE", "entra")
    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)

    with pytest.raises(ValueError, match="ACS_ENDPOINT is required"):
        email_mod.EmailService()


def test_email_service_connection_string_auth_requires_connection_string(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.setenv("ACS_AUTH_MODE", "connection_string")
    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)

    with pytest.raises(ValueError, match="EMAIL_CONNECTION_STRING.*ACS_CONNECTION_STRING"):
        email_mod.EmailService()


def test_sms_service_entra_auth_takes_precedence(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_SMS_FROM_PHONE_NUMBER")
    monkeypatch.setenv("ACS_AUTH_MODE", "entra")
    monkeypatch.setenv("ACS_ENDPOINT", "https://x.communication.azure.com/")
    monkeypatch.setenv("AZURE_SMS_FROM_PHONE_NUMBER", "+15555550100")
    monkeypatch.setenv(
        "AZURE_COMMUNICATION_SMS_CONNECTION_STRING",
        "endpoint=https://x.communication.azure.com/;accesskey=key",
    )
    captured = {}
    credential = SimpleNamespace(name="stub-credential")

    class StubSmsClient:
        def __init__(self, endpoint, supplied_credential):
            captured["endpoint"] = endpoint
            captured["credential"] = supplied_credential

        @classmethod
        def from_connection_string(cls, connection_string):
            captured["connection_string"] = connection_string
            return cls.__new__(cls)

    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)
    monkeypatch.setattr(sms_mod, "SmsClient", StubSmsClient)
    monkeypatch.setattr(sms_mod, "get_credential", lambda: credential)

    service = sms_mod.SmsService()

    assert service.effective_auth_mode == "entra"
    assert captured == {
        "endpoint": "https://x.communication.azure.com/",
        "credential": credential,
    }
    assert service.is_configured() is True


def test_sms_service_auto_prefers_connection_string(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_SMS_FROM_PHONE_NUMBER")
    monkeypatch.setenv("ACS_AUTH_MODE", "auto")
    monkeypatch.setenv("ACS_ENDPOINT", "https://x.communication.azure.com/")
    monkeypatch.setenv("AZURE_SMS_FROM_PHONE_NUMBER", "+15555550100")
    monkeypatch.setenv("ACS_CONNECTION_STRING", "endpoint=https://x;accesskey=key")
    captured = {}

    class StubSmsClient:
        @classmethod
        def from_connection_string(cls, connection_string):
            captured["connection_string"] = connection_string
            return cls.__new__(cls)

    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)
    monkeypatch.setattr(sms_mod, "SmsClient", StubSmsClient)

    service = sms_mod.SmsService()

    assert service.effective_auth_mode == "connection_string"
    assert captured["connection_string"].endswith("accesskey=key")
    assert service.is_configured() is True


def test_sms_service_entra_requires_endpoint(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_SMS_FROM_PHONE_NUMBER")
    monkeypatch.setenv("ACS_AUTH_MODE", "entra")
    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)

    with pytest.raises(ValueError, match="ACS_ENDPOINT is required"):
        sms_mod.SmsService()


def test_sms_service_connection_string_auth_requires_connection_string(monkeypatch):
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_SMS_FROM_PHONE_NUMBER")
    monkeypatch.setenv("ACS_AUTH_MODE", "connection_string")
    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)

    with pytest.raises(ValueError, match="SMS_CONNECTION_STRING.*ACS_CONNECTION_STRING"):
        sms_mod.SmsService()


# ═══════════════════════════════════════════════════════════════════════════════
# OPTIONAL-SERVICE IMPORT SAFETY
# ═══════════════════════════════════════════════════════════════════════════════


def test_invalid_auth_mode_raises_for_explicit_construction(monkeypatch):
    """Explicit construction still fails loudly on an unusable ACS_AUTH_MODE."""
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.setenv("ACS_AUTH_MODE", "totally-bogus")
    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)
    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)

    with pytest.raises(ValueError, match="ACS_AUTH_MODE must be one of"):
        email_mod.EmailService()
    with pytest.raises(ValueError, match="ACS_AUTH_MODE must be one of"):
        sms_mod.SmsService()


def test_non_strict_services_degrade_with_single_warning(monkeypatch, caplog):
    """Non-strict construction degrades to an unconfigured service and warns once."""
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.delenv("AZURE_SMS_FROM_PHONE_NUMBER", raising=False)
    monkeypatch.setenv("ACS_AUTH_MODE", "totally-bogus")
    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)
    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)

    with caplog.at_level("WARNING"):
        email = email_mod.EmailService(strict=False)
        sms = sms_mod.SmsService(strict=False)

    assert email.client is None
    assert email.is_configured() is False
    assert email.effective_auth_mode is None
    assert sms._sms_client is None
    assert sms.is_configured() is False
    assert sms.effective_auth_mode is None

    warnings_logged = [r.getMessage() for r in caplog.records if r.levelname == "WARNING"]
    assert sum("Email service disabled" in m for m in warnings_logged) == 1
    assert sum("SMS service disabled" in m for m in warnings_logged) == 1


def test_module_singletons_are_lazy_and_import_safe(monkeypatch):
    """The legacy module-level singletons resolve lazily and never raise."""
    monkeypatch.setattr(email_mod, "_default_email_service", None)
    monkeypatch.setattr(sms_mod, "_default_sms_service", None)
    _clear_service_auth_environment(monkeypatch, sender_variable="AZURE_EMAIL_SENDER_ADDRESS")
    monkeypatch.delenv("AZURE_SMS_FROM_PHONE_NUMBER", raising=False)
    monkeypatch.setenv("ACS_AUTH_MODE", "totally-bogus")
    monkeypatch.setattr(email_mod, "AZURE_EMAIL_AVAILABLE", True)
    monkeypatch.setattr(sms_mod, "AZURE_SMS_AVAILABLE", True)

    assert email_mod.email_service is email_mod.get_email_service()
    assert sms_mod.sms_service is sms_mod.get_sms_service()
    assert email_mod.is_email_configured() is False
    assert sms_mod.is_sms_configured() is False

    with pytest.raises(AttributeError):
        _ = email_mod.does_not_exist
    with pytest.raises(AttributeError):
        _ = sms_mod.does_not_exist


def test_importing_src_acs_package_with_broken_config_succeeds(monkeypatch):
    """Importing the package must not fail on invalid optional ACS configuration."""
    import subprocess
    import sys

    env = dict(os.environ)
    env["ACS_AUTH_MODE"] = "totally-bogus"
    result = subprocess.run(
        [sys.executable, "-c", "import src.acs; print(src.acs.is_sms_configured())"],
        cwd=str(REPO_ROOT),
        env=env,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr
    assert "False" in result.stdout
