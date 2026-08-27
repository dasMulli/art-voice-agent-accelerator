"""
Email Service for ARTAgent
=========================

Reusable email service that can be used by any tool to send emails via Azure Communication Services.
Supports both plain text and HTML email formats with professional templates.
"""

from __future__ import annotations

import asyncio
import os
import threading
from typing import Any

from utils.azure_auth import get_credential
from utils.ml_logging import get_logger

from src.acs.auth import ACSAuthMode, normalize_acs_auth_mode

# Email service imports
try:
    from azure.communication.email import EmailClient

    AZURE_EMAIL_AVAILABLE = True
except ImportError:
    AZURE_EMAIL_AVAILABLE = False

logger = get_logger("email_service")


class EmailService:
    """Reusable email service for ARTAgent tools."""

    def __init__(self, *, strict: bool = True):
        """
        Initialize the email service with Azure configuration.

        Args:
            strict: When True (default) invalid ACS configuration raises ``ValueError``.
                When False the service degrades to an unconfigured instance and logs
                a single warning, which keeps optional-service imports safe.
        """
        # Try specific email connection string first, then fall back to general ACS connection string
        self.connection_string = os.getenv(
            "AZURE_COMMUNICATION_EMAIL_CONNECTION_STRING"
        ) or os.getenv("ACS_CONNECTION_STRING")
        self.sender_address = os.getenv("AZURE_EMAIL_SENDER_ADDRESS")
        self.endpoint = os.getenv("ACS_ENDPOINT")
        self.auth_mode: ACSAuthMode = "auto"
        self.effective_auth_mode: ACSAuthMode | None = None
        self.credential = None
        self.client: EmailClient | None = None

        if not AZURE_EMAIL_AVAILABLE:
            return

        try:
            self.auth_mode = normalize_acs_auth_mode(os.getenv("ACS_AUTH_MODE"))

            if self.auth_mode == "auto":
                self.effective_auth_mode = (
                    "connection_string" if self.connection_string else "entra"
                )
            else:
                self.effective_auth_mode = self.auth_mode

            if self.effective_auth_mode == "connection_string":
                if not self.connection_string:
                    raise ValueError(
                        "AZURE_COMMUNICATION_EMAIL_CONNECTION_STRING or ACS_CONNECTION_STRING "
                        "is required when ACS_AUTH_MODE=connection_string"
                    )
                self.client = EmailClient.from_connection_string(self.connection_string)
                return

            if not self.endpoint:
                if self.auth_mode == "entra":
                    raise ValueError("ACS_ENDPOINT is required when ACS_AUTH_MODE=entra")
                return

            self.credential = get_credential()
            self.client = EmailClient(self.endpoint, self.credential)
        except ValueError as exc:
            if strict:
                raise
            self.effective_auth_mode = None
            self.credential = None
            self.client = None
            logger.warning("Email service disabled - invalid ACS configuration: %s", exc)

    def is_configured(self) -> bool:
        """Check if email service is properly configured."""
        return AZURE_EMAIL_AVAILABLE and self.client is not None and bool(self.sender_address)

    async def send_email(
        self,
        email_address: str,
        subject: str,
        plain_text_body: str,
        html_body: str | None = None,
    ) -> dict[str, Any]:
        """
        Send email using Azure Communication Services Email.

        Args:
            email_address: Recipient email address
            subject: Email subject line
            plain_text_body: Plain text version of the email
            html_body: Optional HTML version of the email

        Returns:
            Dict containing success status, message ID, and error details if any
        """
        try:
            if not self.is_configured():
                return {
                    "success": False,
                    "error": "Azure Email service not configured or not available",
                }

            # Prepare email message
            message_content = {"subject": subject, "plainText": plain_text_body}

            # Add HTML if provided
            if html_body:
                message_content["html"] = html_body

            message = {
                "senderAddress": self.sender_address,
                "recipients": {"to": [{"address": email_address}]},
                "content": message_content,
            }

            # Send email (offload blocking SDK calls to thread pool)
            def _blocking_send():
                poller = self.client.begin_send(message)
                return poller.result()

            result = await asyncio.to_thread(_blocking_send)

            # Extract message ID
            message_id = getattr(result, "id", None) or getattr(result, "message_id", "unknown")

            logger.info(
                "📧 Email sent successfully to %s, message ID: %s", email_address, message_id
            )
            return {
                "success": True,
                "message_id": message_id,
                "service": "Azure Communication Services Email",
            }

        except Exception as exc:
            logger.error("Email sending failed: %s", exc)
            return {"success": False, "error": f"Azure Email error: {str(exc)}"}

    def send_email_background(
        self,
        email_address: str,
        subject: str,
        plain_text_body: str,
        html_body: str | None = None,
        callback: callable | None = None,
    ) -> None:
        """
        Send email in background thread without blocking the main response.

        Args:
            email_address: Recipient email address
            subject: Email subject line
            plain_text_body: Plain text version of the email
            html_body: Optional HTML version of the email
            callback: Optional callback function to handle the result
        """

        def _send_email_background_task():
            try:
                # Create new event loop for background task
                loop = asyncio.new_event_loop()
                asyncio.set_event_loop(loop)

                # Send the email
                result = loop.run_until_complete(
                    self.send_email(email_address, subject, plain_text_body, html_body)
                )

                # Log result
                if result.get("success"):
                    logger.info(
                        "📧 Background email sent successfully: %s", result.get("message_id")
                    )
                else:
                    logger.warning("📧 Background email failed: %s", result.get("error"))

                # Call callback if provided
                if callback:
                    callback(result)

            except Exception as exc:
                logger.error("Background email task failed: %s", exc, exc_info=True)
            finally:
                loop.close()

        try:
            email_thread = threading.Thread(target=_send_email_background_task, daemon=True)
            email_thread.start()
            logger.info("📧 Email sending started in background thread")
        except Exception as exc:
            logger.error("Failed to start background email thread: %s", exc)


# Lazily-created process-wide email service.
# Import must never fail on optional/invalid ACS configuration, so the shared
# instance is built non-strict on first use.
_default_email_service: EmailService | None = None
_default_email_service_lock = threading.Lock()


def get_email_service() -> EmailService:
    """Return the shared email service, creating it on first use."""
    global _default_email_service
    if _default_email_service is None:
        with _default_email_service_lock:
            if _default_email_service is None:
                _default_email_service = EmailService(strict=False)
    return _default_email_service


def __getattr__(name: str) -> Any:
    """Resolve the legacy module-level ``email_service`` singleton lazily."""
    if name == "email_service":
        return get_email_service()
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


# Convenience functions for easy import
async def send_email(
    email_address: str, subject: str, plain_text_body: str, html_body: str | None = None
) -> dict[str, Any]:
    """Convenience function to send email."""
    return await get_email_service().send_email(email_address, subject, plain_text_body, html_body)


def send_email_background(
    email_address: str,
    subject: str,
    plain_text_body: str,
    html_body: str | None = None,
    callback: callable | None = None,
) -> None:
    """Convenience function to send email in background."""
    get_email_service().send_email_background(
        email_address, subject, plain_text_body, html_body, callback
    )


def is_email_configured() -> bool:
    """Check if email service is configured."""
    return get_email_service().is_configured()
