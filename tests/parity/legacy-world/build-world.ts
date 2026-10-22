// Builds a fictional "world" in a separate PostgreSQL database with the OLD program's own code: the legacy migrations,
// then its real server actions (issue, approve, receipts, cancellation, change, refund, reversal, claims, reserves,
// payments, recoveries, treaties, complaints, AML, month close) run unchanged through its integration hooks, as its own
// scripts/payment-regression.ts does. The data migration tests import this database into SQL Server and reconcile it.
// Development tool only; fictional data only. It drops and recreates only the database named by WORLD_DB.
//
// Usage (needs a checkout of the old program with node_modules, and a local PostgreSQL with SSL on):
//   LEGACY_DIR=/path/to/Insurance-Software PG_ADMIN_URL=postgres://postgres:…@localhost:5432/postgres \
//   APP_DB_PASSWORD=… WORLD_DB=insurance_world \
//   node --import $LEGACY_DIR/scripts/integration/register.ts tests/parity/legacy-world/build-world.ts
import { createHash, randomBytes } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const LEGACY = process.env.LEGACY_DIR ?? '/home/user/legacy-run';
const WORLD = process.env.WORLD_DB ?? 'insurance_world';
if (!/^insurance_world[a-z0-9_]*$/.test(WORLD)) throw new Error('WORLD_DB must start with insurance_world');
const adminUrl = process.env.PG_ADMIN_URL;
if (!adminUrl) throw new Error('PG_ADMIN_URL is not set');
const legacy = (p: string) => import(pathToFileURL(path.join(LEGACY, p)).href);
const { default: postgres } = await import(pathToFileURL(path.join(LEGACY, 'node_modules/postgres/src/index.js')).href);

// ---------------------------------------------------------------- a fresh database with the legacy schema
const server = postgres(adminUrl, { ssl: 'require', max: 1, onnotice: () => {} });
await server.unsafe(`drop database if exists ${WORLD} with (force)`);
await server.unsafe(`create database ${WORLD}`);
await server.end();
const worldUrl = new URL(adminUrl);
worldUrl.pathname = `/${WORLD}`;
const admin = postgres(worldUrl.href, { ssl: 'require', max: 1, onnotice: () => {} });
await admin.unsafe(`
  create schema core;
  grant usage on schema core to core_app, core_reader;
  alter default privileges in schema core grant select, insert, update, delete on tables to core_app;
  alter default privileges in schema core grant usage, select on sequences to core_app;
  alter default privileges in schema core grant select on tables to core_reader;`);
const dir = path.join(LEGACY, 'db/migrations');
for (const file of readdirSync(dir).filter((f) => f.endsWith('.sql')).sort()) {
  await admin.begin(async (tx: any) => {
    await tx.unsafe(readFileSync(path.join(dir, file), 'utf8'));
    await tx.unsafe('create table if not exists core.schema_migration (name text primary key, applied_at timestamptz not null default now())');
    await tx.unsafe(`insert into core.schema_migration (name) values ('${file}') on conflict do nothing`);
  });
}
await admin.unsafe(`revoke update, delete, truncate on core.audit_log, core.premium_transaction, core.payment, core.payment_allocation, core.installment, core.installment_adjustment, core.premium_cession from core_app;
  revoke insert, update, delete on core.schema_migration from core_app; grant select on core.schema_migration to core_app;`);
console.log('legacy schema applied');

// Official rates for every day the world needs (fictional, deterministic), so nothing is fetched from the bank.
await admin.unsafe(`
  insert into core.exchange_rate (currency, rate_date, rate)
  select c, d::date, round((case c when 'USD' then 2.68 else 2.93 end + 0.0001 * (extract(doy from d)::int % 97) + 0.000001 * extract(day from d)::int)::numeric, 6)
  from generate_series('2025-01-01'::date, '2026-12-31'::date, interval '1 day') d cross join (values ('USD'), ('EUR')) v(c)`);

// ---------------------------------------------------------------- the application, on that database
process.env.DB_HOST = worldUrl.hostname;
process.env.DB_PORT = worldUrl.port || '5432';
process.env.DB_NAME = WORLD;
process.env.DB_USER = 'core_app';
process.env.DB_PASSWORD = process.env.APP_DB_PASSWORD ?? '';
const { setSessionToken } = await legacy('scripts/integration/stubs/session.ts');
const { hashPassword } = await legacy('src/lib/password.ts');
const { sql } = await legacy('src/lib/db.ts');

const users: Record<string, string> = {};
const tokens: Record<string, string> = {};
for (const [username, role, name] of [
  ['world.admin', 'admin', 'ადმინი ტესტაძე'], ['world.head', 'operations_head', 'უფროსი ტესტაძე'], ['world.accountant', 'accountant', 'ბუღალტერი ტესტაძე'],
  ['world.sales', 'sales', 'გაყიდვები ტესტაძე'], ['world.claims', 'claims', 'ზარალები ტესტაძე'], ['world.viewer', 'viewer', 'მნახველი ტესტაძე'],
] as const) {
  const [u] = await admin`insert into core.app_user (username, full_name, password_hash, role_code, branch_id)
    values (${username}, ${name}, ${await hashPassword(randomBytes(12).toString('base64url'))}, ${role}, (select id from core.branch where code = 'HQ')) returning id`;
  users[role] = u.id;
  const token = randomBytes(32).toString('base64url');
  await admin`insert into core.session (token_hash, user_id, expires_at) values (${createHash('sha256').update(token).digest('hex')}, ${u.id}, now() + interval '1 day')`;
  tokens[role] = token;
}
const as = (role: string) => setSessionToken(tokens[role]);

const form = (fields: Record<string, string | undefined>) => {
  const f = new FormData();
  for (const [k, v] of Object.entries(fields)) if (v !== undefined) f.append(k, v);
  return f;
};
/** Runs a server action; a redirect is its normal end and gives its target. */
async function run(action: (prev: any, f: FormData) => Promise<any>, fields: Record<string, string | undefined>): Promise<any> {
  try {
    const result = action.length === 1 ? await (action as any)(form(fields)) : await action(null, form(fields));
    return result;
  } catch (e: any) {
    if (e?.to) return { to: e.to as string };
    throw e;
  }
}
const idFrom = (r: any, prefix: string) => {
  const m = new RegExp(`^${prefix}/([0-9a-f-]{36})`).exec(r?.to ?? '');
  if (!m) throw new Error(`expected a redirect to ${prefix}/<id>, got ${JSON.stringify(r)}`);
  return m[1]!;
};
const expectOk = (what: string, r: any) => {
  if (r?.to && /[?&](error|refused|failed|invalid|stale)=/.test(r.to)) throw new Error(`${what}: ${r.to}`);
  if (r?.errors && Object.keys(r.errors).length) throw new Error(`${what}: ${JSON.stringify(r.errors)} ${r.message ?? ''}`);
  if (r?.message && /უფლება|ვერ|შეუძლებელ|დაუშვებელ|არასწორ/.test(r.message)) throw new Error(`${what}: ${r.message}`);
  if (r?.error) throw new Error(`${what}: ${r.error}`);
  return r;
};

const parties = await legacy('src/app/(app)/parties/actions.ts');
const policies = await legacy('src/app/(app)/policies/actions.ts');
const payment = await legacy('src/app/(app)/policies/[id]/payment/actions.ts');
const cancel = await legacy('src/app/(app)/policies/[id]/cancel/actions.ts');
const endorse = await legacy('src/app/(app)/policies/[id]/endorse/actions.ts');
const claims = await legacy('src/app/(app)/claims/actions.ts');
const treaties = await legacy('src/app/(app)/reinsurance/treaties/actions.ts');
const periods = await legacy('src/app/(app)/accounting/periods/actions.ts');
const complaints = await legacy('src/app/(app)/complaints/actions.ts');
const aml = await legacy('src/app/(app)/compliance/aml-cases/actions.ts');

// ---------------------------------------------------------------- clients
as('sales');
const person = (taxId: string, first: string, last: string) =>
  run(parties.saveParty, { kind: 'person', is_resident: 'on', tax_id: taxId, first_name: first, last_name: last, birth_date: '1985-04-12', gender: 'male', phone: '599123456', email: `${taxId}@example.ge`, legal_address: 'თბილისი, ტესტის ქ. 1' });
const clientIds: string[] = [];
for (const [taxId, first, last] of [['01001010101', 'გიორგი', 'ტესტიშვილი'], ['01001010102', 'ნინო', 'მაგალითაძე'], ['01001010103', 'ლევან', 'ნიმუშელი'], ['01001010104', 'მარიამ', 'სანიმუშო']]) {
  clientIds.push(idFrom(expectOk('party', await person(taxId!, first!, last!)), '/parties'));
}
const org = idFrom(expectOk('organization', await run(parties.saveParty, { kind: 'organization', is_resident: 'on', tax_id: '404040404', name: 'შპს ტესტ-ტრანსი', legal_form: 'შპს', state_share_percent: '0', legal_address: 'ბათუმი' })), '/parties');
const reinsurer = idFrom(expectOk('reinsurer', await run(parties.saveParty, { kind: 'organization', is_resident: 'on', tax_id: '505050505', name: 'სატესტო გადამზღვეველი', legal_form: 'სს' })), '/parties');
as('accountant');
for (const id of [...clientIds, org]) expectOk('risk', await run(parties.assessPartyRisk, { party_id: id, mode: 'computed' }));
console.log('clients', clientIds.length + 2);

// ---------------------------------------------------------------- a quota share on CASCO damage
as('admin');
expectOk('treaty', await run(treaties.createTreaty, { name: 'კასკოს კვოტა 2026', kind: 'quota_share', reinsurer_id_number: '505050505', period_from: '2026-01-01', period_to: '2026-12-31', currency: 'GEL', ceded_percent: '30', commission_percent: '20', 'class_5.1': 'on' }));

// ---------------------------------------------------------------- policies
const [product] = await sql`select id from core.product where code = 'CASCO'`;
async function issue(holderTax: string, from: string, to: string, currency: string, plan: string, damage: string, theft?: string, plate = 'TT-001-TT') {
  as('sales');
  const r = expectOk('issue', await run(policies.issuePolicy, {
    product_id: product.id, policyholder_id_number: holderTax, make: 'Toyota', model: 'Prius', year: '2019', plate, value: '30000',
    effective_from: from, effective_to: to, currency, installments: plan,
    cov_damage_on: 'on', cov_damage_premium: damage, cov_damage_limit: '30000', cov_damage_deductible: '300',
    ...(theft ? { cov_theft_on: 'on', cov_theft_premium: theft, cov_theft_limit: '30000' } : {}),
  }));
  const id = idFrom(r, '/policies');
  as('operations_head');
  const answer = await run(policies.approvePolicy, { id });
  if (answer?.outcome !== 'done') throw new Error(`approve ${id}: ${JSON.stringify(answer)}`);
  return id;
}
const p1 = await issue('01001010101', '2026-01-10', '2027-01-10', 'GEL', '4', '1200.00', '300.00', 'AA-101-AA');
const p2 = await issue('01001010102', '2026-02-01', '2027-02-01', 'USD', '1', '850.50', undefined, 'BB-202-BB');
const p3 = await issue('01001010103', '2026-03-15', '2027-03-15', 'EUR', '12', '1440.00', '120.00', 'CC-303-CC');
const p4 = await issue('01001010104', '2026-04-01', '2027-04-01', 'GEL', '1', '999.99', undefined, 'DD-404-DD');
const p5 = await issue('404040404', '2026-05-01', '2027-05-01', 'GEL', '4', '2400.00', '600.00', 'EE-505-EE');
console.log('policies approved');

// ---------------------------------------------------------------- receipts, allocation, overpayment, reversal, refund
as('accountant');
const pay = async (policyId: string, on: string, amount: string, method = 'bank', reference = 'გადახდა') =>
  expectOk('receipt', await run(payment.recordPayment, { policy_id: policyId, received_on: on, amount_gel: amount, method, reference }));
await pay(p1, '2026-01-12', '375.00');
await pay(p1, '2026-04-02', '400.00');
await pay(p2, '2026-02-03', '2300.00');
await pay(p3, '2026-03-20', '500.00', 'cash');
await pay(p4, '2026-04-05', '1100.00');
await pay(p5, '2026-05-03', '750.00', 'online');
// The last receipt of policy 4 is reversed (a correction).
const [rev] = await sql`select p.id from core.payment p join core.payment_allocation a on a.payment_id = p.id join core.installment i on i.id = a.installment_id where i.policy_id = ${p4} and p.kind = 'receipt' limit 1`;
expectOk('reverse', await run(payment.reversePayment, { policy_id: p4, payment_id: rev.id, reason: 'შეცდომით გატარდა' }));
await pay(p4, '2026-04-06', '1100.00');
// Policy 4 is cancelled from 1 July; its overpayment is refunded.
expectOk('cancel', await run(cancel.cancelPolicy, { policy_id: p4, cancel_on: '2026-07-01', reason: 'კლიენტის განცხადება' }));
expectOk('refund', await run(payment.refundOverpayment, { policy_id: p4, method: 'bank', reference: 'დაბრუნება GE00TB0000000000000001' }));
console.log('receipts, reversal, cancellation and refund booked');

// ---------------------------------------------------------------- a change (endorsement) of policy 1, approved
as('sales');
const e1 = await run(endorse.endorsePolicy, { policy_id: p1, change_on: '2026-06-01', reason: 'ქურდობის ლიმიტის გაზრდა', cov_theft_on: 'on', cov_theft_delta: '150.00', cov_theft_limit: '35000', cov_theft_deductible: '' });
const e1id = idFrom(expectOk('endorse', e1), '/policies');
as('operations_head');
const ea = await run(policies.approvePolicy, { id: e1id });
if (ea?.outcome !== 'done') throw new Error(`approve change: ${JSON.stringify(ea)}`);

// ---------------------------------------------------------------- claims
as('claims');
const [line5] = await sql`select l.id from core.policy_line l join core.product_coverage c on c.id = l.product_coverage_id where l.policy_id = ${p5} and c.code = 'damage'`;
const [num5] = await sql`select number from core.policy where id = ${p5}`;
const c1 = idFrom(expectOk('claim', await run(claims.registerClaim, { request_key: randomBytes(16).toString('hex').replace(/(.{8})(.{4})(.{4})(.{4})(.{12})/, '$1-$2-$3-$4-$5'), policy_number: num5.number, occurred_on: '2026-06-10', reported_on: '2026-06-11', description: 'შეჯახება გზაჯვარედინზე', occurred_place: 'თბილისი', policy_line_id: line5.id, initial_reserve: '5000.00', claimed_amount: '6000.00' })), '/claims');
expectOk('reserve', await run(claims.changeReserve, { claim_id: c1, booked_on: '2026-06-20', new_level: '4500.00', reason: 'შეფასების შემდეგ' }));
expectOk('decide', await run(claims.decideClaim, { claim_id: c1, decision: 'approve', approved_amount: '4500.00' }));
as('accountant');
const [holder5] = await sql`select h.tax_id from core.policy p join core.contract c on c.id = p.contract_id join core.party h on h.id = c.policyholder_id where p.id = ${p5}`;
expectOk('pay claim', await run(claims.payClaim, { claim_id: c1, paid_on: '2026-07-01', amount: '2000.00', payee_kind: 'policyholder', payee_id_number: holder5.tax_id, offset_premium: 'on', total_loss_mode: 'none', note: 'პირველი ნაწილი' }));
expectOk('pay claim 2', await run(claims.payClaim, { claim_id: c1, paid_on: '2026-07-15', amount: '2500.00', payee_kind: 'policyholder', payee_id_number: holder5.tax_id, total_loss_mode: 'none', note: 'მეორე ნაწილი' }));
expectOk('salvage', await run(claims.addRecovery, { claim_id: c1, kind: 'salvage', received_on: '2026-08-01', amount_gel: '350.00', note: 'ნაშთის რეალიზაცია' }));
console.log('claim registered, reserved, paid and recovered');

// ---------------------------------------------------------------- complaint, AML case and suspension
as('sales');
const [p1num] = await sql`select number from core.policy where id = ${p1}`;
await run(complaints.registerComplaint, { received_on: '2026-07-02', channel: 'written', party_id_number: '01001010101', customer_name: 'გიორგი ტესტიშვილი', policy_number: p1num.number, category: '2', description: 'გადახდის დაგვიანება' });
as('accountant');
await run(aml.registerCase, { party_id_number: '01001010103', opened_on: '2026-08-10', source: 'monitoring', description: 'უჩვეულოდ დიდი ნაღდი შენატანი', transaction_ref: '', class_code: '5.1' });


// ---------------------------------------------------------------- requests above the financial director's threshold (10 000 GEL)
as('operations_head');
const [resReq] = await sql`select id from core.claim_reserve_request where claim_id = ${c1} order by created_at desc limit 1`;
expectOk('approve reserve', await run(claims.approveReserveRequest, { claim_id: c1, request_id: resReq.id }));
const p6 = await issue('01001010102', '2026-06-01', '2027-06-01', 'GEL', '1', '24000.00', undefined, 'FF-606-FF');
as('accountant');
await pay(p6, '2026-06-02', '24000.00');
expectOk('cancel big', await run(cancel.cancelPolicy, { policy_id: p6, cancel_on: '2026-06-20', reason: 'გადაფორმება' }));
expectOk('refund request', await run(payment.refundOverpayment, { policy_id: p6, method: 'bank', reference: 'დიდი დაბრუნება GE00TB0000000000000002' }));
const [refReq] = await sql`select id from core.premium_refund_request where policy_id = ${p6}`;
if (!refReq) throw new Error('no premium refund request');
as('admin');
expectOk('approve refund', await run(payment.approvePremiumRefundRequest, { policy_id: p6, request_id: refReq.id }));
// A second claim with a payment above the threshold: requested, then authorised.
as('claims');
const [line1] = await sql`select l.id from core.policy_line l join core.product_coverage c on c.id = l.product_coverage_id where l.policy_id = ${p1} and c.code = 'damage'`;
const [num1] = await sql`select number, (select tax_id from core.party where id = (select policyholder_id from core.contract where id = core.policy.contract_id)) as tax from core.policy where id = ${p1}`;
const c2 = idFrom(expectOk('claim 2', await run(claims.registerClaim, { request_key: '7b1f9a3e-0c2d-4e5f-8a6b-9c0d1e2f3a4b', policy_number: num1.number, occurred_on: '2026-08-05', reported_on: '2026-08-06', description: 'სრული განადგურება', occurred_place: 'რუსთავი', policy_line_id: line1.id, initial_reserve: '15000.00', claimed_amount: '15000.00' })), '/claims');
as('operations_head');
const [res2] = await sql`select id from core.claim_reserve_request where claim_id = ${c2} order by created_at desc limit 1`;
if (res2) expectOk('approve reserve 2', await run(claims.approveReserveRequest, { claim_id: c2, request_id: res2.id }));
as('claims');
expectOk('decide 2', await run(claims.decideClaim, { claim_id: c2, decision: 'approve', approved_amount: '15000.00' }));
as('accountant');
console.log('pay c2', JSON.stringify(await run(claims.payClaim, { claim_id: c2, paid_on: '2026-08-20', amount: '12000.00', payee_kind: 'policyholder', payee_id_number: num1.tax, total_loss_mode: 'none', note: 'ავტორიზაციით' })));
const [cpr] = await sql`select id from core.claim_payment_request where claim_id = ${c2}`;
if (!cpr) throw new Error('no claim payment request');
as('admin');
expectOk('approve claim payment', await run(claims.approveClaimPaymentRequest, { claim_id: c2, request_id: cpr.id }));
// The second payment is reversed as a correction.
as('accountant');
const [cp2] = await sql`select id from core.claim_payment where claim_id = ${c2} and kind = 'payment'`;
await run(claims.reverseClaimPayment, { claim_id: c2, claim_payment_id: cp2.id, reason: 'correction', reversed_on: '2026-08-21', note: 'არასწორი თანხა' });

// ---------------------------------------------------------------- a change that reduces premium (schedule reduction)
as('sales');
const e2 = idFrom(expectOk('endorse down', await run(endorse.endorsePolicy, { policy_id: p5, change_on: '2026-09-01', reason: 'ქურდობის დაფარვის მოხსნა', cov_theft_on: 'on', cov_theft_delta: '-200.00', cov_theft_limit: '', cov_theft_deductible: '' })), '/policies');
as('operations_head');
const eb = await run(policies.approvePolicy, { id: e2 });
if (eb?.outcome !== 'done') throw new Error(`approve reduction: ${JSON.stringify(eb)}`);

// ---------------------------------------------------------------- compliance: owner, suspension, sanctions list, groups
as('sales');
await run(parties.addBeneficialOwner, { party_id: org, first_name: 'დავით', last_name: 'მფლობელიძე', criterion: 'share', share_percent: '60', personal_number: '01001010199', citizenship: 'GE', birth_date: '1970-02-02' });
as('accountant');
await run(aml.createSuspension, { party_id: clientIds[2], kind: 'oral', scope: 'all', instructed_at: '2026-09-10T10:30', note: 'ფმს-ის ზეპირი მითითება' });
await admin`insert into core.sanction_list_version (source, generated_at, entries) values ('un', '2026-09-01T00:00:00Z', 2)`;
await admin`insert into core.sanction_list_entry (source, data_id, kind, name, aliases, list_type, reference, listed_on, nationalities, birth_years, documents)
  values ('un', '6908001', 'individual', 'IVAN TESTOV', ${['Ivan Testov', 'ИВАН ТЕСТОВ', 'იოანე ტესტოვი']}, 'UN List', 'QDi.999', '2020-01-15', ${['Testland']}, ${['1970']}, ${['P1234567']}),
         ('un', '6908002', 'entity', 'TEST TRADING LLC', ${['Test Trading "Ltd"']}, 'UN List', 'QDe.999', null, ${[]}, ${[]}, ${[]})`;
as('admin');
const grp = await run((await legacy('src/app/(app)/users/permissions/actions.ts')).createGroupAction, { name: 'ფინანსური დირექტორები' });
const [g] = await sql`select id from core.user_group where name = 'ფინანსური დირექტორები'`;
const perms = await legacy('src/app/(app)/users/permissions/actions.ts');
await run(perms.setGroupGrantAction, { group_id: g.id, permission: 'finance.panel.view', granted: 'true' });
await run(perms.setGroupMemberAction, { group_id: g.id, user_id: users.accountant, member: 'true' });
await run(perms.setUserDecisionAction, { user_id: users.sales, permission: 'audit.log.view', decision: 'allow' });
void grp;
as('sales');
const [cmp] = await sql`select id from core.complaint limit 1`;
await run(complaints.acknowledgeComplaint, { id: cmp.id });
await run(complaints.respondComplaint, { id: cmp.id, status: 'partly_upheld', responded_on: '2026-07-20', response_text: 'გადახდა აღდგა', measures: 'პროცესის შემოწმება', outcome: 'ნაწილობრივ' });

// ---------------------------------------------------------------- months closed
as('accountant');
for (const month of ['2026-01-01', '2026-02-01', '2026-03-01']) {
  const r = await run(periods.closePeriod, { period_start: month });
  if (r?.outcome !== 'closed' && r?.outcome !== 'done') throw new Error(`close ${month}: ${JSON.stringify(r)}`);
}

const counts = await admin`select relname as t, n_live_tup as n from pg_stat_user_tables where schemaname = 'core' order by relname`;
const totals = await admin`
  select (select count(*) from core.policy) policies, (select count(*) from core.premium_transaction) ledger, (select count(*) from core.payment) payments,
         (select count(*) from core.payment_allocation) allocations, (select count(*) from core.claim) claims, (select count(*) from core.audit_log) audit,
         (select count(*) from core.accounting_period where status = 'closed') closed, (select count(*) from core.premium_cession) cessions,
         (select count(*) from core.complaint) complaints, (select count(*) from core.aml_case) aml_cases`;
console.log(JSON.stringify(totals[0]));
void counts;
await sql.end();
await admin.end();
