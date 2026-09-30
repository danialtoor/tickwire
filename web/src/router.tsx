/* eslint-disable react/only-export-components -- route table, not a component module; fast refresh is irrelevant here */
import { lazy, Suspense, type ReactNode } from 'react'
import { createBrowserRouter } from 'react-router'
import { Layout } from './components/Layout'
import { Landing } from './pages/Landing'

const Trader = lazy(() => import('./pages/Trader'))
const Connect = lazy(() => import('./pages/Connect'))
const Analyzer = lazy(() => import('./pages/Analyzer'))
const OpsPage = lazy(() => import('./pages/Ops'))
const MarketData = lazy(() => import('./pages/MarketData'))

const page = (node: ReactNode) => (
  <Suspense fallback={<div className="p-8 text-sm text-muted">Loading…</div>}>{node}</Suspense>
)

export const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <Landing /> },
      { path: 'trade', element: page(<Trader />) },
      { path: 'connect', element: page(<Connect />) },
      { path: 'analyzer', element: page(<Analyzer />) },
      { path: 'ops', element: page(<OpsPage />) },
      { path: 'data', element: page(<MarketData />) },
      { path: '*', element: <Landing /> },
    ],
  },
])
