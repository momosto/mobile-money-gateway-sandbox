// Fires 1,000 payment requests from 100 virtual users at 100 distinct orders, so every
// order is requested about 10 times, all at once. Then it asks the provider how many
// references it charged more than once. The answer must be zero.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const GATEWAY = __ENV.GATEWAY_URL || 'http://localhost:5080';
const PROVIDER = __ENV.PROVIDER_URL || 'http://localhost:5090';
const ORDERS = 100;
const replays = new Counter('idempotent_replays');

export const options = {
  scenarios: { burst: { executor: 'shared-iterations', vus: 100, iterations: 1000, maxDuration: '2m' } },
  thresholds: { checks: ['rate==1.0'] },
};

// setup() runs once; init code runs once per virtual user, so the run id must come from
// here or each user would invent its own keys for the "same" order.
export function setup() {
  http.post(`${PROVIDER}/v1/reset`);
  return { run: Date.now().toString(36) };
}

export default function (data) {
  // iterationInTest is global across virtual users; __ITER would restart at 0 for each one.
  const order = `${data.run}-order-${exec.scenario.iterationInTest % ORDERS}`;
  const res = http.post(`${GATEWAY}/payments`, JSON.stringify({ msisdn: '263771234567', amountCents: 2500, currency: 'USD' }), {
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': 'demo-merchant-key', 'Idempotency-Key': order },
  });
  check(res, { 'payment accepted or replayed (201/200)': (r) => r.status === 201 || r.status === 200 });
  if (res.headers['Idempotent-Replayed'] === 'true') replays.add(1);
}

export function teardown() {
  sleep(5); // let in-flight provider calls land
  const stats = http.get(`${PROVIDER}/v1/stats`).json();
  console.log(`provider stats: ${JSON.stringify(stats)}`);
  check(stats, {
    'no order was charged twice': (s) => s.referencesChargedMoreThanOnce === 0,
    'provider saw at most one charge per order': (s) => s.charges <= ORDERS,
    'most orders reached the provider (not all: it fails some on purpose)': (s) => s.references >= ORDERS * 0.85,
  });
}
