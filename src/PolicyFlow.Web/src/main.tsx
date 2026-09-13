import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { AgentPage } from './pages/AgentPage';
import { HomePage } from './pages/HomePage';
import { LookupPage } from './pages/LookupPage';
import { NewQuotePage } from './pages/NewQuotePage';
import { PolicyPage } from './pages/PolicyPage';
import { QuoteStatusPage } from './pages/QuoteStatusPage';
import { UnderwritingPage } from './pages/UnderwritingPage';
import './styles/theme.css';
import './styles/app.css';

const container = document.getElementById('root');

if (!container) {
  throw new Error('Root element #root was not found in index.html.');
}

createRoot(container).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<Layout />}>
          <Route path="/" element={<HomePage />} />
          <Route path="/quotes/new" element={<NewQuotePage />} />
          <Route path="/quotes/:quoteId" element={<QuoteStatusPage />} />
          <Route path="/policies/:policyId" element={<PolicyPage />} />
          <Route path="/lookup" element={<LookupPage />} />
          <Route path="/agent" element={<AgentPage />} />
          <Route path="/underwriting" element={<UnderwritingPage />} />
          {/* Unknown paths go home rather than showing a blank page. */}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
);
