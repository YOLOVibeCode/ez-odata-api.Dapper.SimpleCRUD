using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;

var builder = WebApplication.CreateBuilder(args);
var shop = builder.Configuration.GetConnectionString("shop") ?? throw new InvalidOperationException("Set ConnectionStrings:shop.");

builder.Services.AddEzOData(ez =>
{
    ez.AddService("shop", s => s.UseSqlite(shop));
    ez.AllowAnonymousInDevelopment();
});

var app = builder.Build();
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");
app.UseEzODataSwaggerUI();
DropInDiagnostics.Map(app); // measurement only, not part of the app
app.Run();
