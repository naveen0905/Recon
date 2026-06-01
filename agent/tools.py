"""
Tool implementations for the Recon agent.

Each function is an async stub that calls the C# Recon API via httpx.
All calls include team and engagement_id to enforce scope isolation.
"""

import logging
import os
from typing import Any

import httpx

logger = logging.getLogger(__name__)

_RECON_API_URL: str = os.environ.get("RECON_API_URL", "http://localhost:5000")


def _client() -> httpx.AsyncClient:
    """Return a short-lived async HTTP client pointed at the Recon API."""
    return httpx.AsyncClient(base_url=_RECON_API_URL, timeout=30.0)


async def describe_sources(team: str) -> dict[str, Any]:
    """Return the list of configured sources for *team*."""
    logger.info("tool=describe_sources team=%s", team)
    async with _client() as client:
        response = await client.get(
            "/api/sources",
            headers={"X-Team": team},
        )
        response.raise_for_status()
        return response.json()


async def query_assets(
    team: str,
    engagement_id: str,
    source_id: str,
    query_id: str,
    parameters: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Query assets for *team* within *engagement_id*, scoped to *source_id*."""
    logger.info(
        "tool=query_assets team=%s engagement_id=%s source_id=%s query_id=%s",
        team, engagement_id, source_id, query_id,
    )
    async with _client() as client:
        response = await client.post(
            "/api/assets/query",
            json={
                "team": team,
                "engagement_id": engagement_id,
                "source_id": source_id,
                "query_id": query_id,
                "parameters": parameters or {},
            },
            headers={"X-Team": team},
        )
        response.raise_for_status()
        return response.json()


async def get_asset_history(team: str, asset_id: str) -> dict[str, Any]:
    """Return the change history for a single asset."""
    logger.info("tool=get_asset_history team=%s asset_id=%s", team, asset_id)
    async with _client() as client:
        response = await client.get(
            f"/api/assets/{asset_id}/history",
            headers={"X-Team": team},
        )
        response.raise_for_status()
        return response.json()


async def trigger_pull(team: str, source_id: str) -> dict[str, Any]:
    """Trigger an on-demand connector pull for *source_id*."""
    logger.info("tool=trigger_pull team=%s source_id=%s", team, source_id)
    async with _client() as client:
        response = await client.post(
            f"/api/sources/{source_id}/pull",
            json={"team": team},
            headers={"X-Team": team},
        )
        response.raise_for_status()
        return response.json()


async def get_stale_assets(team: str) -> dict[str, Any]:
    """Return assets whose last pull exceeded their configured stale-after threshold."""
    logger.info("tool=get_stale_assets team=%s", team)
    async with _client() as client:
        response = await client.get(
            "/api/assets/stale",
            headers={"X-Team": team},
        )
        response.raise_for_status()
        return response.json()
