/** A signed-in agent, as issued by the simulated identity provider. */
export interface AgentSession {
  agentId: string;
  accessToken: string;
  expiresAtMs: number;
}

const STORAGE_KEY = 'policyflow.agentSession';

/**
 * Treat a token as expired this long before it actually is, so a request never
 * leaves with a token that lapses before the API validates it.
 */
const EXPIRY_MARGIN_MS = 60_000;

/**
 * The agent's sign-in, kept in `sessionStorage`.
 *
 * **POC only, like the dev-token endpoint that issues it.** `sessionStorage`
 * rather than `localStorage` so the sign-in ends with the tab, but any token
 * JavaScript can read is exposed to an XSS flaw. Production would sign in through
 * Microsoft Entra ID with MSAL, keep tokens in memory, and refresh silently.
 *
 * Every access is wrapped: storage can throw (private browsing, disabled storage),
 * and a broken storage must mean "signed out", not a crashed page.
 */
export function loadAgentSession(nowMs = Date.now()): AgentSession | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<AgentSession>;
    const isValid =
      typeof parsed.agentId === 'string' &&
      typeof parsed.accessToken === 'string' &&
      typeof parsed.expiresAtMs === 'number';

    if (!isValid || parsed.expiresAtMs! - EXPIRY_MARGIN_MS <= nowMs) {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }

    return parsed as AgentSession;
  } catch {
    return null;
  }
}

export function saveAgentSession(session: AgentSession): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  } catch {
    // Storage unavailable: the sign-in lasts for this page view only.
  }
}

export function clearAgentSession(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to clear.
  }
}
