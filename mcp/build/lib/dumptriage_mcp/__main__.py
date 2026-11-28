"""`dumptriage-mcp`: the stdio server an agent launches. Configuration is the environment: DUMPTRIAGE_API_KEY (required)
and DUMPTRIAGE_API_URL (optional, for a self-hosted engine)."""
from .server import build_server


def main() -> None:
    build_server().run("stdio")


if __name__ == "__main__":
    main()
