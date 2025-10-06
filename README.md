# Brandt's Playlist - Backend (Plex Proxy API)

## What it is


A small, personal, same‑origin proxy for Plex Media Server (PMS).
It provides a minimal API that the frontend player uses to:
- List available playlists.
- Retrieve tracks for a selected playlist.
- Mint short‑lived stream tickets.
- Serve audio streams through same‑origin URLs.

This approach avoids CORS and mixed‑content issues, keeps Plex tokens private, and enables controlled ticket lifetimes for resilient playback.

---

## How it works

### High‑level flow
1. Playlist listing
   - Queries PMS for user playlists.
   - Returns normalized summaries: { id, title }.
   - Endpoint: GET /plexproxy/api/playlists

2. Playlist detail
   - Fetches playlist items from PMS.
   - Returns { title, count, tracks[] } where each track includes:
     - ratingKey (or equivalent ID)
     - title, artist, album
     - cover hints (thumb, art, artUrl)
   - Endpoint: GET /plexproxy/api/playlist/:id

3. Ticket minting
   - Frontend requests a fresh ticket for each track:
     - GET /plexproxy/api/stream/for/:ratingKey → { "ticket": "<opaque>" }
   - Backend caches { ticket → { ratingKey, createdAt, ttl } }.

4. Streaming
   - Frontend sets <audio>.src to /plexproxy/api/stream/:ticket?rk=:ratingKey
   - Backend validates the ticket and:
     - Proxies audio bytes from PMS (recommended), or
     - Issues a 302 redirect to PMS (optional).
   - Tickets expire after TICKET_TTL_SEC (e.g., 120s) and are cleaned up periodically.

5. Security
   - Plex token (PLEX_TOKEN) is never exposed to the client.
   - PMS calls are server‑side; only normalized metadata and proxied streams are returned.

---

## Endpoints summary
- GET /plexproxy/api/playlists
  → { playlists: [ { id, title } ] }

- GET /plexproxy/api/playlist/:id
  → { title, count, tracks: [ { ratingKey, title, artist, album, thumb|artUrl } ] }

- GET /plexproxy/api/stream/for/:ratingKey
  → { ticket: "<opaque>" }

- GET /plexproxy/api/stream/:ticket?rk=:ratingKey
  → Audio stream (proxy or redirect)

---

## Configuration (environment)

    PORT=8080
    PROXY_PATH_PREFIX=/plexproxy
    API_PREFIX=/api

    PLEX_BASE_URL=http://<pms-host>:32400
    PLEX_TOKEN=<your-plex-token>

    TICKET_TTL_SEC=120
    MAX_CACHE=5000
    STREAM_PROXY_MODE=proxy   # proxy | redirect

---

## Operational notes
- Keep the backend private (LAN or authenticated).
- Ensure system clock accuracy for ticket TTL.
- Do not log Plex tokens or full signed URLs.
- Return minimal metadata; the frontend only needs track fields and cover hints.

---

## Running locally

    cp .env.example .env   # set PLEX_BASE_URL and PLEX_TOKEN
    npm install
    npm start              # serves /plexproxy/api/... on http://localhost:8080

Serve the frontend from the same origin or configure a rewrite so /plexproxy/* routes to this backend.

---

## License
Personal use only. No warranty.
