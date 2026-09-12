import { NavLink, Outlet } from 'react-router-dom';

/**
 * Application shell: demo banner, header, main region, footer.
 *
 * A single navigation row rather than a hamburger menu. There are three
 * destinations; a collapsed menu would add a tap and a state machine to save no
 * horizontal space worth having.
 */
export function Layout() {
  return (
    <div className="app-shell">
      {/* First focusable element: lets a keyboard user bypass the nav. */}
      <a className="skip-link" href="#main">
        Skip to main content
      </a>

      {/* Permanent and unmissable. The carriers and premiums in this app are
          invented, and must never be mistakable for real insurance quotes. */}
      <div className="demo-banner" role="note">
        Demonstration only — all carriers, rates and premiums shown are fictional.
      </div>

      <header className="site-header">
        <div className="site-header__inner">
          <NavLink to="/" className="brand">
            <span className="brand__mark" aria-hidden="true">
              PF
            </span>
            <span>
              PolicyFlow
              <span className="brand__sub">Auto insurance marketplace</span>
            </span>
          </NavLink>

          <nav className="site-nav" aria-label="Main navigation">
            <NavLink to="/quotes/new" className="site-nav__link">
              New quote
            </NavLink>
            <NavLink to="/lookup" className="site-nav__link">
              Find my quote
            </NavLink>
          </nav>
        </div>
      </header>

      <main className="app-main" id="main">
        <Outlet />
      </main>

      <footer className="site-footer">
        <div className="site-footer__inner">
          <p>
            <strong>PolicyFlow</strong> — a vendor-neutral proof of concept.
            React + .NET 10 + SQL Server (EF Core) + MongoDB (EF Core) + Azure
            Service Bus.
          </p>
          <p style={{ marginTop: 'var(--space-2)' }}>
            The five carriers, their rating factors and every premium shown are
            fictional and exist to demonstrate software architecture. Nothing here
            represents real insurance pricing, a real rate filing, or a real
            company.
          </p>
        </div>
      </footer>
    </div>
  );
}
