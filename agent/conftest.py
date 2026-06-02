"""pytest configuration — add the agent directory to sys.path so modules are importable."""
import sys
import os

sys.path.insert(0, os.path.dirname(__file__))
