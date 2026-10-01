using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Entities.AspNetCore;
using EzOdata.EntityFrameworkCore.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var shop = builder.Configuration.GetConnectionString("shop") ?? throw new InvalidOperationException("Set ConnectionStrings:shop.");

builder.Services.AddEzOData(ez =>
{
    ez.AddService("shop", s => s.UseSqlite(shop));
    ez.AllowAnonymousInDevelopment();
});
builder.Services.ExtendEzOData(x => x.Service("shop", s => s
    .UseEfCore<ShopDb>()
    .Table<Customer>(t => t.BeforeInsert((c, ctx) => { if (string.IsNullOrWhiteSpace(c.Name)) ctx.Reject("full_name is required."); }))));

var app = builder.Build();
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");
app.UseEzODataSwaggerUI();
DropInDiagnostics.Map(app); // measurement only, not part of the app
app.Run();
