"""
Recon Agent Service — FastAPI entry point.

Startup:
  - Loads config from agent/config.yaml
  - Validates Entra ID Bearer token on every request except GET /health
  - Enforces that the token's "team" claim matches the request body's team field

Authentication middleware:
  - Authorization: Bearer <entra-id-token>
  - Token must contain a "team" claim
  - The team claim must match QueryRequest.team; otherwise 403 is returned

Run locally:
  uvicorn main:app --reload --port 8000
"""

import logging
import os
import pathlib
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator

import yaml
from fastapi import FastAPI, HTTPException, Request, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from orchestrator import QueryRequest, QueryResponse, run

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

_config: dict[str, Any] = {}


def _load_config() -> dict[str, Any]:
    config_path = pathlib.Path(__file__).parent / "config.yaml"
    if config_path.exists():
        with config_path.open() as fh:
            return yaml.safe_load(fh) or {}
    logger.warning("config.yaml not found at %s — using defaults", config_path)
    return {}


# ---------------------------------------------------------------------------
# Lifespan
# ---------------------------------------------------------------------------


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    global _config
    _config = _load_config()
    logger.info(
        "Agent service started — provider=%s model=%s",
        _config.get("provider", "anthropic"),
        _config.get("model", "claude-sonnet-4-6"),
    )
    yield
    logger.info("Agent service shutting down")


# ---------------------------------------------------------------------------
# App
# ---------------------------------------------------------------------------

app = FastAPI(
    title="Recon Agent Service",
    version="0.2.0",
    lifespan=lifespan,
)

# ---------------------------------------------------------------------------
# Auth middleware
# ---------------------------------------------------------------------------


def _extract_team_claim(token: str) -> str | None:
    """
    Decode the 'team' claim from an Entra ID JWT without full signature
    verification (signature verification requires the JWKS endpoint; here we
    perform structural validation and claim extraction only).

    In production, replace with a proper MSAL / PyJWT + JWKS validation step.
    """
    import base64
    import json

    parts = token.split(".")
    if len(parts) != 3:
        return None
    # JWT payload is the second part, base64url-encoded
    payload_b64 = parts[1]
    # Pad to a multiple of 4
    padding = 4 - len(payload_b64) % 4
    if padding != 4:
        payload_b64 += "=" * padding
    try:
        payload = json.loads(base64.urlsafe_b64decode(payload_b64))
        return payload.get("team")
    except Exception:
        return None


@app.middleware("http")
async def auth_middleware(request: Request, call_next: Any) -> Any:
    """
    Validate Entra ID Bearer token on every request except GET /health.
    Rejects with 401 if no/invalid token, 403 if the team claim mismatches.
    """
    if request.method == "GET" and request.url.path in ("/health", "/api/health"):
        return await call_next(request)

    auth_header = request.headers.get("Authorization", "")
    if not auth_header.startswith("Bearer "):
        return JSONResponse(
            status_code=status.HTTP_401_UNAUTHORIZED,
            content={"detail": "Missing or invalid Authorization header"},
        )

    token = auth_header.removeprefix("Bearer ").strip()
    token_team = _extract_team_claim(token)
    if token_team is None:
        return JSONResponse(
            status_code=status.HTTP_401_UNAUTHORIZED,
            content={"detail": "Token missing required 'team' claim"},
        )

    # For /query we enforce team claim == request body team
    if request.method == "POST" and request.url.path == "/query":
        # We need the body to compare — stash the parsed team for the route
        request.state.token_team = token_team

    return await call_next(request)


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/query", response_model=QueryResponse)
async def query(request_body: QueryRequest, request: Request) -> QueryResponse:
    """
    Answer a recon intelligence question for the given team and engagement.

    The Entra ID token's 'team' claim must match *request_body.team*.
    """
    token_team: str | None = getattr(request.state, "token_team", None)
    if token_team is not None and token_team != request_body.team:
        logger.warning(
            "auth: team claim mismatch token_team=%s request_team=%s",
            token_team, request_body.team,
        )
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Token team claim does not match request team",
        )

    max_iterations: int = int(_config.get("max_tool_iterations", 10))

    logger.info(
        "query: team=%s engagement_id=%s model=%s",
        request_body.team, request_body.engagement_id, request_body.model,
    )

    response = await run(request_body, max_iterations=max_iterations)
    return response
