"""
Query builder — uses Claude to produce a structured QueryPlan from a natural language question.

The planner inspects the team's sources catalog and returns a QueryPlan that suggests
which tool to call, which source and query to use, and what parameters to pass.
The plan is advisory; the orchestrator loop may still decide differently.
"""

import json
import logging
import os
import re
from typing import Any

import anthropic
from pydantic import BaseModel

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

KNOWN_TOOLS = [
    "describe_sources",
    "query_assets",
    "get_asset_history",
    "trigger_pull",
    "get_stale_assets",
]

_DEFAULT_MODEL = "claude-sonnet-4-6"

# ---------------------------------------------------------------------------
# Models
# ---------------------------------------------------------------------------


class QueryPlan(BaseModel):
    tool: str
    source_id: str | None = None
    query_id: str | None = None
    parameters: dict[str, Any] = {}
    reasoning: str


# ---------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------


async def build_query_plan(
    question: str,
    team: str,
    sources_catalog: list[dict[str, Any]],
) -> QueryPlan:
    """
    Call Claude to produce a QueryPlan for *question*.

    Returns a fallback plan (tool=describe_sources) if:
    - Claude's response cannot be parsed as JSON
    - The suggested tool is not in KNOWN_TOOLS
    - tool=query_assets but source_id or query_id is empty
    """
    model = os.environ.get("AGENT_MODEL", _DEFAULT_MODEL)
    api_key = os.environ.get("ANTHROPIC_API_KEY", "")

    client = anthropic.AsyncAnthropic(api_key=api_key)

    catalog_text = json.dumps(sources_catalog, indent=2) if sources_catalog else "[]"

    system = (
        "You are a query planner for a recon intelligence platform. "
        "Given a natural language question, you select the best tool and parameters "
        "from the available tools and source catalog. "
        "Respond ONLY with a JSON code block containing your plan — no prose outside the block."
    )

    user_message = f"""Team: {team}

Available tools:
- describe_sources: List all configured recon sources available to the team.
- query_assets: Query recon assets (requires source_id and query_id from the catalog).
- get_asset_history: Get the change history for a single asset (requires asset_id in parameters).
- trigger_pull: Trigger an immediate connector pull for a source (requires source_id).
- get_stale_assets: Return assets whose last-pull timestamp exceeds the stale threshold.

Sources catalog:
{catalog_text}

Question: {question}

Respond with a JSON code block in this exact format:
```json
{{
  "tool": "<one of the 5 known tools>",
  "source_id": "<source id or null>",
  "query_id": "<query id or null>",
  "parameters": {{}},
  "reasoning": "<brief explanation>"
}}
```"""

    try:
        response = await client.messages.create(
            model=model,
            max_tokens=512,
            system=system,
            messages=[{"role": "user", "content": user_message}],
        )

        raw_text = ""
        for block in response.content:
            if block.type == "text":
                raw_text = block.text
                break

        plan = _parse_plan(raw_text)

    except Exception as exc:
        logger.warning("query_builder: failed to call Claude: %s", exc)
        return QueryPlan(tool="describe_sources", reasoning="Could not parse plan")

    return _validate_plan(plan)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _parse_plan(text: str) -> QueryPlan:
    """Extract and parse a JSON code block from *text*."""
    # Try to find ```json ... ``` block
    match = re.search(r"```json\s*(\{.*?\})\s*```", text, re.DOTALL)
    if match:
        json_str = match.group(1)
    else:
        # Fallback: try to find a bare JSON object
        match = re.search(r"\{.*\}", text, re.DOTALL)
        if match:
            json_str = match.group(0)
        else:
            logger.warning("query_builder: no JSON found in Claude response")
            return QueryPlan(tool="describe_sources", reasoning="Could not parse plan")

    try:
        data = json.loads(json_str)
        return QueryPlan(**data)
    except (json.JSONDecodeError, TypeError, ValueError) as exc:
        logger.warning("query_builder: JSON parse error: %s", exc)
        return QueryPlan(tool="describe_sources", reasoning="Could not parse plan")


def _validate_plan(plan: QueryPlan) -> QueryPlan:
    """Validate the plan and fall back to describe_sources if invalid."""
    if plan.tool not in KNOWN_TOOLS:
        logger.warning(
            "query_builder: unknown tool '%s' — falling back to describe_sources", plan.tool
        )
        return QueryPlan(
            tool="describe_sources",
            reasoning=f"Could not parse plan: unknown tool '{plan.tool}'",
        )

    if plan.tool == "query_assets":
        if not plan.source_id or not plan.query_id:
            logger.warning(
                "query_builder: query_assets requires source_id and query_id — falling back"
            )
            return QueryPlan(
                tool="describe_sources",
                reasoning="Could not parse plan: query_assets requires source_id and query_id",
            )

    return plan
