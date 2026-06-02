"""Tests for query_builder.py"""
import json
import pytest
from unittest.mock import AsyncMock, patch, MagicMock
from query_builder import build_query_plan, QueryPlan, KNOWN_TOOLS


@pytest.mark.asyncio
async def test_known_tools_are_defined():
    assert len(KNOWN_TOOLS) == 5
    assert "query_assets" in KNOWN_TOOLS


@pytest.mark.asyncio
async def test_build_query_plan_fallback_on_bad_json():
    # Patch anthropic client to return unparseable text
    with patch("query_builder.anthropic.AsyncAnthropic") as MockClient:
        instance = MockClient.return_value
        instance.messages.create = AsyncMock(return_value=MagicMock(
            content=[MagicMock(type="text", text="not json")]
        ))
        plan = await build_query_plan("find open ports", "team-a", [])
    assert plan.tool == "describe_sources"
    assert "Could not parse" in plan.reasoning


@pytest.mark.asyncio
async def test_build_query_plan_invalid_tool_falls_back():
    bad_plan = json.dumps({
        "tool": "delete_everything",
        "source_id": None,
        "query_id": None,
        "parameters": {},
        "reasoning": "x",
    })
    with patch("query_builder.anthropic.AsyncAnthropic") as MockClient:
        instance = MockClient.return_value
        instance.messages.create = AsyncMock(return_value=MagicMock(
            content=[MagicMock(type="text", text=f"```json\n{bad_plan}\n```")]
        ))
        plan = await build_query_plan("find open ports", "team-a", [])
    assert plan.tool == "describe_sources"


@pytest.mark.asyncio
async def test_build_query_plan_query_assets_missing_source_falls_back():
    """query_assets without source_id should fall back to describe_sources."""
    bad_plan = json.dumps({
        "tool": "query_assets",
        "source_id": None,
        "query_id": None,
        "parameters": {},
        "reasoning": "needs source",
    })
    with patch("query_builder.anthropic.AsyncAnthropic") as MockClient:
        instance = MockClient.return_value
        instance.messages.create = AsyncMock(return_value=MagicMock(
            content=[MagicMock(type="text", text=f"```json\n{bad_plan}\n```")]
        ))
        plan = await build_query_plan("find open ports", "team-a", [])
    assert plan.tool == "describe_sources"
    assert "Could not parse" in plan.reasoning


@pytest.mark.asyncio
async def test_build_query_plan_valid_describe_sources():
    """A valid describe_sources plan is returned as-is."""
    good_plan = json.dumps({
        "tool": "describe_sources",
        "source_id": None,
        "query_id": None,
        "parameters": {},
        "reasoning": "listing sources",
    })
    with patch("query_builder.anthropic.AsyncAnthropic") as MockClient:
        instance = MockClient.return_value
        instance.messages.create = AsyncMock(return_value=MagicMock(
            content=[MagicMock(type="text", text=f"```json\n{good_plan}\n```")]
        ))
        plan = await build_query_plan("what sources do I have?", "team-a", [])
    assert plan.tool == "describe_sources"
    assert plan.reasoning == "listing sources"


@pytest.mark.asyncio
async def test_build_query_plan_client_exception_falls_back():
    """If the Claude client raises, fall back gracefully."""
    with patch("query_builder.anthropic.AsyncAnthropic") as MockClient:
        instance = MockClient.return_value
        instance.messages.create = AsyncMock(side_effect=RuntimeError("network error"))
        plan = await build_query_plan("find open ports", "team-a", [])
    assert plan.tool == "describe_sources"
    assert "Could not parse" in plan.reasoning
