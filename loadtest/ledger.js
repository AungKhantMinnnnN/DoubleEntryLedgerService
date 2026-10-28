// k6 load test for the ledger API. Pick a scenario with -e SCENARIO=steady|contention|mixed.
//   k6 run -e SCENARIO=steady -e BASE_URL=http://localhost:8080 loadtest/ledger.js
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const SCENARIO = __ENV.SCENARIO || 'steady';
const VUS = Number(__ENV.VUS || 32);
const DURATION = __ENV.DURATION || '60s';

const posted = new Counter('entries_posted');      // 201s
const conflicts = new Rate('conflict_rate');       // 409s: contention retries exhausted, share of post attempts
const errors = new Rate('error_rate');             // anything that is not 201/409 on a post, or not 200 on a read
const postMs = new Trend('post_ms', true);
const readMs = new Trend('read_ms', true);

// Scenario shape: how many accounts, and how a VU chooses what to do.
const SHAPES = {
  steady: { accounts: 200, writeShare: 1.0 },      // many accounts, writes only: little contention
  contention: { accounts: 5, writeShare: 1.0 },    // every VU hammers the same 5 accounts
  mixed: { accounts: 200, writeShare: 0.2 },       // 80% reads (balance, history), 20% writes
};
const shape = SHAPES[SCENARIO];
if (!shape) throw new Error(`unknown SCENARIO ${SCENARIO}`);

export const options = {
  scenarios: { [SCENARIO]: { executor: 'constant-vus', vus: VUS, duration: DURATION } },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const json = { headers: { 'Content-Type': 'application/json' } };

export function setup() {
  const run = Date.now().toString(36);
  const accounts = [];
  for (let i = 0; i < shape.accounts; i++) {
    // Even index = asset, odd = revenue; all USD, so no FX in the hot path.
    const type = i % 2 === 0 ? 'Asset' : 'Revenue';
    const r = http.post(`${BASE}/accounts`, JSON.stringify({ code: `lt-${SCENARIO}-${run}-${i}`, type, currency: 'USD' }), json);
    if (r.status !== 201) throw new Error(`setup failed: ${r.status} ${r.body}`);
    accounts.push(r.json('id'));
  }
  return { run, accounts };
}

const pick = (a) => a[Math.floor(Math.random() * a.length)];

function post(d) {
  let debit = pick(d.accounts), credit = pick(d.accounts);
  while (credit === debit) credit = pick(d.accounts);
  const amount = (100 + Math.floor(Math.random() * 10000)) / 100; // 1.00 .. 101.00; integer cents, so no float noise (4-decimal limit)
  const body = JSON.stringify({
    reference: `lt-${SCENARIO}`,
    lines: [
      { accountId: debit, direction: 'Debit', amount },
      { accountId: credit, direction: 'Credit', amount },
    ],
  });
  const res = http.post(`${BASE}/journal-entries`, body, {
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': `${d.run}-${__VU}-${__ITER}` },
  });
  postMs.add(res.timings.duration);
  if (res.status === 201) posted.add(1);
  conflicts.add(res.status === 409);
  errors.add(res.status !== 201 && res.status !== 409);
  if (res.status !== 201 && res.status !== 409 && __ITER < 3) console.error(`unexpected ${res.status}: ${res.body}`);
}

function read(d) {
  const id = pick(d.accounts);
  const path = Math.random() < 0.5 ? `/accounts/${id}/balance` : `/accounts/${id}/transactions?limit=20`;
  const res = http.get(`${BASE}${path}`);
  readMs.add(res.timings.duration);
  errors.add(!check(res, { 'read 200': (r) => r.status === 200 }));
}

export default function (d) {
  if (Math.random() < shape.writeShare) post(d); else read(d);
}
