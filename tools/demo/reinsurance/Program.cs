using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Web.Pages.Parties;

var mode=args.FirstOrDefault()??"inspect";
if(mode is not("inspect" or "seed" or "verify"))throw new Exception("Unknown operation");
var connection=Environment.GetEnvironmentVariable("INSURANCE_ADMIN_CONNECTION")??throw new Exception("Missing explicit demo connection");
var cs=new SqlConnectionStringBuilder(connection);
if(cs.DataSource!="sql6033.site4now.net" || cs.InitialCatalog!="db_ad0152_instest")throw new Exception("Only the explicitly named demo database is allowed");
var app=new Db(new DbOptions{ConnectionString=connection});
await using var control=await app.OpenAsync();
if(await control.ExecuteScalarAsync<string>("select db_name()")!="db_ad0152_instest")throw new Exception("Wrong target database");
const string TreatyName="დემო QS 40% — ავტოდაზღვევა";
const string ReinsurerKey="DEMO-RI-REINSURER-20261023";
var rates=new Rates(app,new DemoHttpFactory());
var today=app.Today();
var month=new DateOnly(today.Year,today.Month,1);
if(mode=="inspect")
{
    Console.WriteLine(JsonSerializer.Serialize(new{Database="db_ad0152_instest",Treaties=await control.QueryAsync("select number,name,kind,status,period_from,period_to,ceded_percent from core.ri_treaty order by number"),ClosedMonths=await control.QueryAsync("select period_start from core.accounting_period where status=N'closed'"),ExistingDemoHistoryHash=await PriorHistoryHashAsync()}));
    return;
}
if(mode=="seed")await SeedAsync();
await VerifyAsync();

async Task<string> PriorHistoryHashAsync()
{
    var json=await control.ExecuteScalarAsync<string>("""
        select p.id,p.number,p.status,p.premium_currency,
        (select l.id,l.premium,l.premium_gel,(select t.* from core.premium_transaction t where t.policy_line_id=l.id order by t.id for json path) ledger,
            (select ce.* from core.premium_cession ce join core.premium_transaction t on t.id=ce.premium_transaction_id where t.policy_line_id=l.id order by ce.id for json path) cessions
         from core.policy_line l where l.policy_id=p.id order by l.id for json path) lines,
        (select c.id,c.status,c.approved_amount,(select cc.* from core.claim_cession cc where cc.claim_id=c.id order by cc.treaty_id for json path) cessions,
            (select cp.* from core.claim_payment cp where cp.claim_id=c.id order by cp.id for json path) payments,
            (select cr.* from core.claim_reserve cr where cr.claim_id=c.id order by cr.id for json path) reserves
         from core.claim c where c.policy_id=p.id order by c.id for json path) claims
        from core.policy p join core.insured_object o on o.policy_id=p.id
        where json_value(o.attributes,'$.plate') like N'DEMO%' order by p.id for json path
        """);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json??"")));
}

async Task SeedAsync()
{
    var locked=await control.ExecuteScalarAsync<int>("declare @r int; exec @r=sys.sp_getapplock @Resource=N'demo.reinsurance.20261023.v1',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0; select @r;");
    if(locked<0)throw new Exception("Another reinsurance demo loader is running");
    if(await control.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status=N'closed' and period_start=@month",new{month})!=0)throw new Exception("Current month is closed; no demo financial records were written");
    var before=await PriorHistoryHashAsync();
    var actor=await control.QuerySingleAsync<CurrentUser>("select u.id Id,u.username Username,u.full_name FullName,u.role_code RoleCode,r.name_ka RoleName from core.app_user u join core.role r on r.code=u.role_code where u.username=N'admin' and u.is_active=1");
    if(await control.ExecuteScalarAsync<int>("select count(*) from core.party where foreign_id=@ReinsurerKey",new{ReinsurerKey})==0)
    {
        var party=await PartyForm.SaveAsync(app,actor,new Dictionary<string,string>{["kind"]="organization",["name"]="დემო გადამზღვეველი — QS40",["foreign_id"]=ReinsurerKey,["country_code"]="DE",["actual_address"]="ფიქტიური გადამზღვეველი — მხოლოდ დემოსთვის",["email"]="reinsurer@example.invalid"},_=>false);
        if(party.SavedId is null)throw new Exception("Reinsurer creation refused: "+JsonSerializer.Serialize(party));
    }
    var treaty=await control.QuerySingleOrDefaultAsync<Guid?>("select id from core.ri_treaty where name=@TreatyName",new{TreatyName});
    if(treaty is null)
    {
        var created=await Treaties.CreateAsync(app,actor,new Dictionary<string,string>{["name"]=TreatyName,["reinsurer_id_number"]=ReinsurerKey,["kind"]="quota_share",["period_from"]=Iso(today),["period_to"]=Iso(today.AddYears(1)),["currency"]="GEL",["ceded_percent"]="40",["commission_percent"]="0",["broker_percent"]="0",["note"]="DEMO-RI-20261023: ფიქტიური 40% კვოტური გადაზღვევა; ძველი პოლისების ისტორია არ გადაითვლება."},["5.1"]);
        treaty=created.Id??throw new Exception("Treaty creation refused: "+JsonSerializer.Serialize(created));
    }
    Console.WriteLine("Fictional reinsurer and 40% quota-share treaty prepared.");
    var product=await control.ExecuteScalarAsync<Guid>("select id from core.product where code=N'CASCO' and is_active=1");
    var coverages=await app.ReadTxAsync(tx=>PolicyActions.CoveragesAsync(tx,product));
    for(var i=0;i<2;i++)
    {
        var plate=$"RI-DEMO-{i+1:D2}";
        var policy=await control.QuerySingleOrDefaultAsync<Guid?>("select policy_id from core.insured_object where json_value(attributes,'$.plate')=@plate",new{plate});
        var premium=i==0?6000m:3000m;
        if(policy is null)
        {
            var values=new Dictionary<string,string>{["product_id"]=product.ToString(),["policyholder_id_number"]=$"DEMO-20261023-CLIENT-{i+7:D2}",["make"]="Toyota",["model"]="DEMO — გადაზღვევის მაგალითი",["year"]="2024",["plate"]=plate,["value"]="60000",["effective_from"]=Iso(today),["effective_to"]=Iso(today.AddYears(1)),["currency"]="GEL",["installments"]="4"};
            foreach(var c in coverages){values[$"cov_{c.Code}_premium"]=Money(premium);values[$"cov_{c.Code}_limit"]="60000";values[$"cov_{c.Code}_deductible"]="0";}
            var issued=await PolicyActions.IssuePolicyAsync(app,rates,actor,values,code=>code=="damage");
            if(issued.Redirect is null)throw new Exception("Demo policy refused: "+JsonSerializer.Serialize(issued));
            policy=Guid.Parse(issued.Redirect.Split('/')[2].Split('?')[0]);
        }
        if(await control.ExecuteScalarAsync<string>("select status from core.policy where id=@policy",new{policy})=="pending_approval")
        {
            var approved=await PolicyActions.ApprovePolicyAsync(app,actor,policy.ToString());
            if(approved.Outcome!="done")throw new Exception("Demo approval refused: "+JsonSerializer.Serialize(approved));
        }
        var receiptRef=$"DEMO-RI-20261023-PAY-{i+1:D2}";
        if(await control.ExecuteScalarAsync<int>("select count(*) from core.payment where reference=@receiptRef",new{receiptRef})==0)
        {
            var paid=await PaymentActions.RecordPaymentAsync(app,rates,actor,new Dictionary<string,string>{["policy_id"]=policy.ToString()!,["amount_gel"]=Money(premium),["received_on"]=Iso(today),["method"]="bank",["reference"]=receiptRef});
            if(paid.Redirect is null)throw new Exception("Demo receipt refused: "+JsonSerializer.Serialize(paid));
        }
        var description=$"DEMO-RI-20261023-CLAIM-{i+1:D2}: გადამზღვევლის 40%-იანი წილის მაგალითი";
        var claim=await control.QuerySingleOrDefaultAsync<Guid?>("select id from core.claim where policy_id=@policy and description=@description",new{policy,description});
        if(claim is null)
        {
            var number=await control.ExecuteScalarAsync<string>("select number from core.policy where id=@policy",new{policy});
            var line=await control.ExecuteScalarAsync<Guid>("select l.id from core.policy_line l join core.product_coverage c on c.id=l.product_coverage_id where l.policy_id=@policy and c.code=N'damage'",new{policy});
            var registered=await ClaimActions.RegisterClaimAsync(app,actor,Form(("policy_number",number!),("occurred_on",Iso(today)),("reported_on",Iso(today)),("occurred_place","დემო — თბილისი"),("description",description),($"line:{line}","on"),($"reserve:{line}",i==0?"5000":"2000")));
            if(registered.RedirectTo is null)throw new Exception("Demo claim refused: "+JsonSerializer.Serialize(registered));
            claim=Guid.Parse(registered.RedirectTo.Split('/')[2].Split('?')[0]);
        }
        if(i==0)
        {
            var status=await control.ExecuteScalarAsync<string>("select status from core.claim where id=@claim",new{claim});
            if(status is "registered" or "in_review")await ClaimActions.DecideClaimAsync(app,actor,Form(("claim_id",claim.ToString()!),("decision","approve"),("approved_amount","5000")),today);
            if(await control.ExecuteScalarAsync<int>("select count(*) from core.claim_payment where claim_id=@claim",new{claim})==0)
            {
                var payment=await ClaimActions.PayClaimAsync(app,rates,actor,Form(("claim_id",claim.ToString()!),("amount","2000"),("paid_on",Iso(today)),("payee_kind","policyholder"),("total_loss_mode","none"),("note","DEMO-RI-20261023: ნაწილობრივი ანაზღაურება")),today);
                if(payment.Errors.Count>0 || payment.Message is not null || await control.ExecuteScalarAsync<int>("select count(*) from core.claim_payment where claim_id=@claim",new{claim})!=1)throw new Exception("Demo claim settlement refused: "+JsonSerializer.Serialize(payment));
            }
        }
        Console.WriteLine($"Reinsured policy/claim example {i+1}/2 ready.");
    }
    var after=await PriorHistoryHashAsync();
    if(after!=before)throw new Exception("Prior demo history changed during loading; inspect before further work");
    Console.WriteLine("Previous demo policy/claim financial history hash unchanged: "+after);
}

async Task VerifyAsync()
{
    var treatyId=await control.QuerySingleAsync<Guid>("select id from core.ri_treaty where name=@TreatyName",new{TreatyName});
    var statement=await app.ReadTxAsync(tx=>Bordereau.BuildAsync(tx,rates,treatyId,month,month.AddMonths(1).AddDays(-1)))??throw new Exception("Missing treaty statement");
    var t=statement.Totals;
    if(statement.Premiums.Count!=2 || statement.Claims.Count!=2 || t.CededGel!=360000 || t.ClaimsPaidGel!=80000 || t.ReserveGel!=200000 || t.BalanceGel!=280000)throw new Exception("Demo bordereau did not match the independent expected 40% amounts: "+JsonSerializer.Serialize(t));
    var policies=await control.QueryAsync("select p.id,p.number,p.status,o.description,(select sum(premium) from core.policy_line where policy_id=p.id) premium_gel from core.policy p join core.insured_object o on o.policy_id=p.id where json_value(o.attributes,'$.plate') like N'RI-DEMO-%' order by p.number");
    var claims=await control.QueryAsync("select id,number,policy_id,status,approved_amount from core.claim where description like N'DEMO-RI-20261023-CLAIM-%' order by number");
    Console.WriteLine(JsonSerializer.Serialize(new{TreatyId=treatyId,TreatyNumber=statement.Treaty.Number,Name=TreatyName,SharePercent=40,From=Iso(statement.From),To=Iso(statement.To),ExpectedTotalsGel=new{GrossPremium=9000,CededPremium=3600,RetainedPremium=5400,GrossClaimsPaid=2000,ReinsurerPaidShare=800,GrossOutstandingReserve=5000,ReinsurerReserveShare=2000,BordereauBalance=2800},VerifiedTotalsTetri=t,Policies=policies,Claims=claims,Bordereau=statement,PriorDemoHistoryHash=await PriorHistoryHashAsync()},new JsonSerializerOptions{WriteIndented=true}));
}
static string Iso(DateOnly date)=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
static string Money(decimal value)=>value.ToString("0.00",CultureInfo.InvariantCulture);
static PostedForm Form(params (string Key,string Value)[] fields)=>new(fields.Select(x=>new KeyValuePair<string,string?>(x.Key,x.Value)));
sealed class DemoHttpFactory:IHttpClientFactory{public HttpClient CreateClient(string name)=>new();}
