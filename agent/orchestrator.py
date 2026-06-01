"""
Agent orchestrator — drives a Claude tool-use loop to answer recon queries.

Scope enforcement is applied at two levels:
  1. System prompt: Claude is instructed to stay within the engagement scope.
  2. Tool dispatch: every tool call automatically receives the team and
     engagement_id from the original request; the agent cannot override them.
"""

import logging
import os
from typing import Any

import anthropic

from tools import (
    describe_sources,
    get_asset_history,
    get_stale_assets,
    query_assets,
    trigger_pull,
)

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Tool schema definitions (sent to Claude so it knows how to call each tool)
# ---------------------------------------------------------------------------

TOOL_DEFINITIONS: list[dict[str, Any]] = [
    {
        "name": "describe_sources",
        "description": (
            "List all configured recon sources available to the team. "
            "Returns source IDs, types, and metadata."
        ),
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": [],
        },
    },
    {
        "name": "query_assets",
        "description": (
            "Query recon assets for the team within the current engagement. "
            "Provide a source_id and query_id, plus any filter parameters."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "source_id": {
                    "type": "string",
                    "description": "The source to query (from describe_sources).",
                },
                "query_id": {
                    "type": "string",
                    "description": "Named query defined in the source config.",
                },
                "parameters": {
                    "type": "object",
                    "description": "Optional key-value filter parameters for the query.",
                },
            },
            "required": ["source_id", "query_id"],
        },
    },
    {
        "name": "get_asset_history",
        "description": "Return the change history for a single asset by asset_id.",
        "input_schema": {
            "type": "object",
            "properties": {
                "asset_id": {
                    "type": "string",
                    "description": "Stable asset identifier (team::dedup_key).",
                },
            },
            "required": ["asset_id"],
        },
    },
    {
        "name": "trigger_pull",
        "description": "Trigger an immediate connector pull for a specific source.",
        "input_schema": {
            "type": "object",
            "properties": {
                "source_id": {
                    "type": "string",
                    "description": "The source to pull from.",
                },
            },
            "required": ["source_id"],
        },
    },
    {
        "name": "get_stale_assets",
        "description": (
            "Return assets whose last-pull timestamp exceeds the source's "
            "stale-after-days threshold."
        ),
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": [],
        },
    },
]


# ---------------------------------------------------------------------------
# Models (imported from main to avoid circular import — defined here as well)
# ---------------------------------------------------------------------------

from pydantic import BaseModel


class QueryRequest(BaseModel):
    team: str
    engagement_id: str
    question: str
    model: str = "claude-sonnet-4-6"


class QueryResponse(BaseModel):
    answer: str
    sources_used: list[str] = []
    assets: list[dict[str, Any]] = []


# ---------------------------------------------------------------------------
# Orchestrator
# ---------------------------------------------------------------------------


async def run(request: QueryRequest, max_iterations: int = 10) -> QueryResponse:
    """
    Drive a Claude tool-use loop to answer *request.question*.

    Scope enforcement:
      - System prompt restricts Claude to the team's engagement scope.
      - All tool dispatches inject team + engagement_id from the request,
        ignoring any attempt by Claude to use different values.
    """
    api_key = os.environ.get("ANTHROPIC_API_KEY", "")
    if not api_key:
        raise RuntimeError("ANTHROPIC_API_KEY environment variable is not set")

    client = anthropic.AsyncAnthropic(api_key=api_key)

    system_prompt = (
        f"You are a recon intelligence assistant for team {request.team}. "
        f"You have access to tools to query recon assets within engagement "
        f"{request.engagement_id}. "
        "Never return assets outside the engagement scope. "
        "If a tool call would expose data for a different team, refuse and explain why."
    )

    messages: list[dict[str, Any]] = [
        {"role": "user", "content": request.question},
    ]

    sources_used: list[str] = []
    collected_assets: list[dict[str, Any]] = []
    answer: str = ""

    for iteration in range(max_iterations):
        logger.info(
            "orchestrator: iteration=%d team=%s engagement_id=%s",
            iteration, request.team, request.engagement_id,
        )

        response = await client.messages.create(
            model=request.model,
            max_tokens=4096,
            system=system_prompt,
            tools=TOOL_DEFINITIONS,  # type: ignore[arg-type]
            messages=messages,  # type: ignore[arg-type]
        )

        # Collect any text content
        for block in response.content:
            if block.type == "text":
                answer = block.text

        if response.stop_reason == "end_turn":
            logger.info("orchestrator: stop_reason=end_turn after %d iteration(s)", iteration + 1)
            break

        if response.stop_reason != "tool_use":
            logger.warning("orchestrator: unexpected stop_reason=%s", response.stop_reason)
            break

        # Append assistant turn
        messages.append({"role": "assistant", "content": response.content})  # type: ignore[arg-type]

        # Process tool calls
        tool_results: list[dict[str, Any]] = []
        for block in response.content:
            if block.type != "tool_use":
                continue

            tool_name: str = block.name
            tool_input: dict[str, Any] = block.input  # type: ignore[assignment]
            tool_use_id: str = block.id

            logger.info(
                "orchestrator: dispatching tool=%s team=%s engagement_id=%s",
                tool_name, request.team, request.engagement_id,
            )

            result = await _dispatch_tool(
                tool_name=tool_name,
                tool_input=tool_input,
                team=request.team,
                engagement_id=request.engagement_id,
            )

            sources_used.append(tool_name)
            if isinstance(result, dict) and "assets" in result:
                collected_assets.extend(result["assets"])

            tool_results.append(
                {
                    "type": "tool_result",
                    "tool_use_id": tool_use_id,
                    "content": str(result),
                }
            )

        messages.append({"role": "user", "content": tool_results})
    else:
        logger.warning(
            "orchestrator: reached max_iterations=%d without end_turn", max_iterations
        )

    return QueryResponse(
        answer=answer,
        sources_used=list(dict.fromkeys(sources_used)),  # deduplicate, preserve order
        assets=collected_assets,
    )


# ---------------------------------------------------------------------------
# Tool dispatch (scope-safe: team + engagement_id always come from the request)
# ---------------------------------------------------------------------------


async def _dispatch_tool(
    tool_name: str,
    tool_input: dict[str, Any],
    team: str,
    engagement_id: str,
) -> Any:
    """
    Execute the named tool.  team and engagement_id are always injected from
    the original request — Claude cannot override them.
    """
    match tool_name:
        case "describe_sources":
            return await describe_sources(team=team)

        case "query_assets":
            return await query_assets(
                team=team,
                engagement_id=engagement_id,
                source_id=tool_input.get("source_id", ""),
                query_id=tool_input.get("query_id", ""),
                parameters=tool_input.get("parameters"),
            )

        case "get_asset_history":
            asset_id: str = tool_input.get("asset_id", "")
            # Scope check: asset_id must be prefixed with the team
            if not asset_id.startswith(f"{team}::"):
                logger.warning(
                    "orchestrator: scope violation blocked — asset_id=%s does not belong to team=%s",
                    asset_id, team,
                )
                return {"error": "Access denied: asset does not belong to your team."}
            return await get_asset_history(team=team, asset_id=asset_id)

        case "trigger_pull":
            return await trigger_pull(
                team=team,
                source_id=tool_input.get("source_id", ""),
            )

        case "get_stale_assets":
            return await get_stale_assets(team=team)

        case _:
            logger.error("orchestrator: unknown tool requested: %s", tool_name)
            return {"error": f"Unknown tool: {tool_name}"}
