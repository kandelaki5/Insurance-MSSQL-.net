using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Web.Pages.Parties;

var mode = args.FirstOrDefault() ?? "inspect";
if (mode is not ("inspect" or "seed" or "verify")) throw new Exception("Unknown mode");
var connection = Environment.GetEnvironmentVariable("INSURANCE_ADMIN_CONNECTION") ?? throw new Exception("Missing explicit connection");
var builder = new SqlConnectionStringBuilder(connection);
if (builder.DataSource != "sql6033.site4now.net" || builder.InitialCatalog != "db_ad0152_instest") throw new Exception("Only the named new demo database is allowed");
var app = new Db(new DbOptions { ConnectionString = connection });
await using var control = await app.OpenAsync();
if (await control.ExecuteScalarAsync<string>("select db_name()") != "db_ad0152_instest") throw new Exception("Wrong target");
if (mode == "inspect")
{
    Console.WriteLine(JsonSerializer.Serialize(new {
        Database="db_ad0152_instest",
        Counts=await control.QuerySingleAsync("select (select count(*) from core.party) Parties, (select count(*) from core.policy) Policies, (select count(*) from core.payment) Payments, (select count(*) from core.claim) Claims, (select count(*) from core.accounting_period where status=N'closed') ClosedMonths"),
        Products=await control.QueryAsync("select p.code, pc.code coverage, pc.class_code, pc.is_mandatory from core.product p join core.product_coverage pc on pc.product_id=p.id order by p.code,pc.sort_order"),
        Admin=await control.QueryAsync("select username,role_code from core.app_user where username=N'admin'")
    }));
    return;
}
if (mode == "seed") await SeedAsync();
await VerifyAsync();

async Task SeedAsync()
{
    var lockResult=await control.ExecuteScalarAsync<int>("declare @r int; exec @r=sys.sp_getapplock @Resource=N'demo.dataset.20261023.v1',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0; select @r;");
    if(lockResult<0) throw new Exception("A demo data loader is already running");
    var user=await control.QuerySingleAsync<CurrentUser>("select u.id Id,u.username Username,u.full_name FullName,u.role_code RoleCode,r.name_ka RoleName from core.app_user u join core.role r on r.code=u.role_code where u.username=N'admin' and u.is_active=1");
    var today=app.Today();
    var first=new DateOnly(today.Year,today.Month,1);
    if(await control.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status=N'closed' and period_start>=@first",new{first=first.AddMonths(-2)})>0)
        throw new Exception("The demonstration date range contains closed months; no data was seeded");
    var product=await control.QuerySingleAsync<Guid>("select id from core.product where code=N'CASCO' and is_active=1");
    var coverages=await app.ReadTxAsync(tx=>PolicyActions.CoveragesAsync(tx,product));
    var rates=new Rates(app,new DemoHttpFactory());
    var partyIds=new List<Guid>();
    for(var i=0;i<10;i++)
    {
        var key=$"DEMO-20261023-CLIENT-{i+1:D2}";
        var exists=await control.QuerySingleOrDefaultAsync<Guid?>("select id from core.party where foreign_id=@key",new{key});
        Guid party;
        if(exists is {} found) party=found;
        else
        {
            var organization=i>=6;
            var values=new Dictionary<string,string>{["kind"]=organization?"organization":"person",["foreign_id"]=key,["country_code"]="GE",
                ["first_name"]="დემო "+new[]{"გიორგი","ნინო","ლევანი","მარიამი","დათო","სალომე"}[i%6],["last_name"]="ტესტაშვილი",
                ["name"]="დემო "+new[]{"ავტოპარკი","ლოგისტიკა","მოგზაური","სავაჭრო ჯგუფი"}[i%4],
                ["actual_address"]="სატესტო მისამართი — დემო",["email"]=$"demo{i+1:D2}@example.invalid"};
            var saved=await PartyForm.SaveAsync(app,user,values,_=>false);
            if(saved.SavedId is not {} savedId) throw new Exception("Party creation refused: "+JsonSerializer.Serialize(saved));
            party=savedId;
            await app.AsUserAsync(user.Id,async tx=>{
                var assessment=await PartyRisk.ComputePartyRiskAsync(tx,party,today) ?? throw new Exception("Risk facts missing");
                await PartyRisk.RecordPartyRiskAsync(tx,party,user.Id,today,assessment.Assessment,null);return 0;
            });
        }
        partyIds.Add(party);
    }
    Console.WriteLine("10 marked fictional clients prepared through party and risk services.");
    var policyIds=new List<Guid>();
    for(var i=0;i<12;i++)
    {
        var plate=$"DEMO{i+1:D3}";
        var existing=await control.QuerySingleOrDefaultAsync<Guid?>("select policy_id from core.insured_object where json_value(attributes,'$.plate')=@plate",new{plate});
        Guid policy;
        if(existing is {} found) policy=found;
        else
        {
            var from=i>=10?today:first.AddMonths(-(i%3)).AddDays(i%8);
            var values=new Dictionary<string,string>{["product_id"]=product.ToString(),["policyholder_id_number"]=$"DEMO-20261023-CLIENT-{i%10+1:D2}",
                ["make"]=new[]{"Toyota","Hyundai","Kia","Ford"}[i%4],["model"]="DEMO სატესტო ავტომობილი",["year"]="2023",["plate"]=plate,
                ["value"]=(25000+i*3000).ToString(CultureInfo.InvariantCulture),["effective_from"]=Iso(from),["effective_to"]=Iso(from.AddYears(1)),
                ["currency"]="GEL",["installments"]=new[]{"1","4","12"}[i%3],["cancel_grace_days"]="30"};
            foreach(var coverage in coverages)
            {
                values[$"cov_{coverage.Code}_premium"]=(1200+i*300).ToString(CultureInfo.InvariantCulture);
                values[$"cov_{coverage.Code}_limit"]=(25000+i*3000).ToString(CultureInfo.InvariantCulture);
                values[$"cov_{coverage.Code}_deductible"]="100";
            }
            var issued=await PolicyActions.IssuePolicyAsync(app,rates,user,values,code=>code=="damage" || (i%3==0 && code=="mtpl") || (i%4==0 && code=="accident"));
            if(issued.Redirect is null) throw new Exception("Policy issue refused: "+JsonSerializer.Serialize(issued));
            policy=Guid.Parse(issued.Redirect.Split('/')[2].Split('?')[0]);
        }
        policyIds.Add(policy);
        if(i<10)
        {
            var status=await control.QuerySingleAsync<string>("select status from core.policy where id=@policy",new{policy});
            if(status=="pending_approval")
            {
                var approved=await PolicyActions.ApprovePolicyAsync(app,user,policy.ToString());
                if(approved.Outcome!="done") throw new Exception("Policy approval refused: "+JsonSerializer.Serialize(approved));
            }
        }
        if(i<9 && i is not (2 or 5))
        {
            var reference=$"DEMO-20261023-PAY-{i+1:D2}";
            if(await control.ExecuteScalarAsync<int>("select count(*) from core.payment where reference=@reference",new{reference})==0)
            {
                var premium=await control.ExecuteScalarAsync<decimal>("select sum(premium) from core.policy_line where policy_id=@policy",new{policy});
                var amount=i switch {1=>premium/4,3=>premium+200,4=>premium/2,_=>premium};
                var payment=await PaymentActions.RecordPaymentAsync(app,rates,user,new Dictionary<string,string>{["policy_id"]=policy.ToString(),["amount_gel"]=amount.ToString("0.00",CultureInfo.InvariantCulture),["received_on"]=Iso(today),["method"]="bank",["reference"]=reference});
                if(payment.Redirect is null) throw new Exception("Payment refused: "+JsonSerializer.Serialize(payment));
            }
        }
        Console.WriteLine($"Policy {i+1:D2}/12 ready.");
    }
    for(var i=0;i<4;i++)
    {
        var policy=policyIds[new[]{0,1,3,4}[i]];
        var description=$"DEMO-20261023-CLAIM-{i+1:D2}: სატესტო ავტოსაგზაო შემთხვევა";
        var existing=await control.QuerySingleOrDefaultAsync<Guid?>("select id from core.claim where policy_id=@policy and description=@description",new{policy,description});
        Guid claim;
        if(existing is {} found) claim=found;
        else
        {
            var number=await control.ExecuteScalarAsync<string>("select number from core.policy where id=@policy",new{policy});
            var line=await control.ExecuteScalarAsync<Guid>("select top(1) l.id from core.policy_line l join core.product_coverage c on c.id=l.product_coverage_id where policy_id=@policy order by case when c.code=N'damage' then 0 else 1 end,l.id",new{policy});
            var registered=await ClaimActions.RegisterClaimAsync(app,user,Form(("policy_number",number!),("occurred_on",Iso(today.AddDays(-1))),("reported_on",Iso(today)),("occurred_place","დემო — თბილისი"),("description",description),($"line:{line}","on"),($"reserve:{line}",(1200+i*600).ToString(CultureInfo.InvariantCulture))));
            if(registered.RedirectTo is null) throw new Exception("Claim registration refused: "+JsonSerializer.Serialize(registered));
            claim=Guid.Parse(registered.RedirectTo.Split('/')[2].Split('?')[0]);
        }
        var status=await control.ExecuteScalarAsync<string>("select status from core.claim where id=@claim",new{claim});
        if(i==1 && status=="registered") await ClaimActions.DecideClaimAsync(app,user,Form(("claim_id",claim.ToString()),("decision","review")),today);
        if(i>=2)
        {
            if(status is "registered" or "in_review") await ClaimActions.DecideClaimAsync(app,user,Form(("claim_id",claim.ToString()),("decision","approve"),("approved_amount",i==2?"2500":"3500")),today);
            if(await control.ExecuteScalarAsync<int>("select count(*) from core.claim_payment where claim_id=@claim",new{claim})==0)
            {
                var paid=await ClaimActions.PayClaimAsync(app,rates,user,Form(("claim_id",claim.ToString()),("amount",i==2?"1000":"3500"),("paid_on",Iso(today)),("payee_kind","policyholder"),("total_loss_mode","none"),("note","DEMO-20261023: სატესტო ანაზღაურება")),today);
                if(paid.Errors.Count>0 || paid.Message is not null) throw new Exception("Claim payment refused: "+JsonSerializer.Serialize(paid));
                if(await control.ExecuteScalarAsync<int>("select count(*) from core.claim_payment where claim_id=@claim",new{claim})==0) throw new Exception("Claim payment was not settled; approval may be required");
            }
        }
        Console.WriteLine($"Claim {i+1:D2}/4 ready.");
    }
}

async Task VerifyAsync()
{
    var mismatches=await control.QuerySingleAsync<(int Schedule,int Premium,int Receipt,int Claim)>("""
        select
          (select count(*) from core.policy p join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'DEMO%'
           and coalesce((select sum(amount) from core.installment where policy_id=p.id),0)<>coalesce((select sum(premium) from core.policy_line where policy_id=p.id),0)),
          (select count(*) from core.policy p join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'DEMO%' and p.status=N'active'
           and coalesce((select sum(t.amount) from core.premium_transaction t join core.policy_line l on l.id=t.policy_line_id where l.policy_id=p.id),0)<>coalesce((select sum(premium) from core.policy_line where policy_id=p.id),0)),
          (select count(*) from core.payment p where reference like N'DEMO-20261023-PAY-%' and coalesce((select sum(amount_gel) from core.payment_allocation where payment_id=p.id),0)>p.amount_gel),
          (select count(*) from core.claim_payment p join core.claim c on c.id=p.claim_id where c.description like N'DEMO-20261023-CLAIM-%' and p.amount<>p.net_paid+p.deductible_withheld+p.premium_offset)
        """);
    if(mismatches != (0,0,0,0)) throw new Exception("Demo financial reconciliation failed: "+mismatches);
    var summary=await control.QuerySingleAsync("""
        select
        (select count(*) from core.party where foreign_id like N'DEMO-20261023-CLIENT-%') DemoClients,
        (select count(*) from core.policy p join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'DEMO%') DemoPolicies,
        (select count(*) from core.payment where reference like N'DEMO-20261023-PAY-%') DemoReceipts,
        (select count(*) from core.claim where description like N'DEMO-20261023-CLAIM-%') DemoClaims,
        (select count(*) from core.claim_payment cp join core.claim c on c.id=cp.claim_id where c.description like N'DEMO-20261023-CLAIM-%') DemoClaimPayments,
        (select coalesce(sum(amount_gel),0) from core.payment where reference like N'DEMO-20261023-PAY-%') DemoReceiptsGel,
        (select coalesce(sum(cp.net_paid_gel),0) from core.claim_payment cp join core.claim c on c.id=cp.claim_id where c.description like N'DEMO-20261023-CLAIM-%') DemoClaimNetPaidGel
        """);
    var policies=await control.QueryAsync("select p.id,p.number,p.status,p.effective_from,p.effective_to,(select sum(l.premium) from core.policy_line l where l.policy_id=p.id) PremiumGel from core.policy p join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'DEMO%' order by json_value(o.attributes,'$.plate')");
    var claims=await control.QueryAsync("select id,number,status,approved_amount from core.claim where description like N'DEMO-20261023-CLAIM-%' order by description");
    var financial=await control.QuerySingleAsync("""
        select
        (select sum(l.premium) from core.policy_line l join core.policy p on p.id=l.policy_id join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'DEMO%' and p.status=N'active') ActivePremiumGel,
        (select coalesce(sum(a.amount_gel),0) from core.payment_allocation a join core.payment p on p.id=a.payment_id where p.reference like N'DEMO-20261023-PAY-%') AllocatedReceiptsGel,
        (select coalesce(sum(r.change),0) from core.claim_reserve r join core.claim c on c.id=r.claim_id where c.description like N'DEMO-20261023-CLAIM-%') OutstandingClaimReserveGel
        """);
    Console.WriteLine(JsonSerializer.Serialize(new{Summary=summary,Financial=financial,Reconciliation=new{ScheduleErrors=mismatches.Schedule,PremiumErrors=mismatches.Premium,ReceiptErrors=mismatches.Receipt,ClaimPaymentErrors=mismatches.Claim},Policies=policies,Claims=claims},new JsonSerializerOptions{WriteIndented=true}));
}
static string Iso(DateOnly date)=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
static PostedForm Form(params (string Key,string Value)[] fields)=>new(fields.Select(x=>new KeyValuePair<string,string?>(x.Key,x.Value)));
sealed class DemoHttpFactory:IHttpClientFactory { public HttpClient CreateClient(string name)=>new(); }
