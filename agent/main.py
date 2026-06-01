from fastapi import FastAPI
from pydantic import BaseModel

app = FastAPI(title="Recon Agent Service", version="0.1.0")


class QueryRequest(BaseModel):
    query: str
    engagement_id: str | None = None


class QueryResponse(BaseModel):
    answer: str
    sources: list[str] = []


@app.get("/health")
async def health() -> dict:
    return {"status": "healthy"}


@app.post("/query", response_model=QueryResponse)
async def query(request: QueryRequest) -> QueryResponse:
    # Stub — full implementation in Task 5.1 / 5.2
    raise NotImplementedError("Agent orchestrator not yet implemented (Task 5.2)")
