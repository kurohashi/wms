using Microsoft.EntityFrameworkCore;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Dispatching;
using Dit.Shared.Cqrs.Handlers;
using Wms.PickList.Api.Endpoints;
using Wms.PickList.Application;
using Wms.Persistence.DbContexts;
using Scalar.AspNetCore;
using FluentValidation;
using Dit.Shared.Exceptions.Extensions;
using Wms.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

var writeConnectionString = builder.Configuration.GetConnectionString("WriteConnection")
    ?? throw new InvalidOperationException("Connection string 'WriteConnection' is not configured.");

builder.Services.AddDbContext<WmsDbContextWrite>(opt =>
{
    opt.UseNpgsql(writeConnectionString, o => o.UseNetTopologySuite());
});

builder.Services.Scan(scan =>
{
    scan.FromAssemblies(PickListApplicationAssembly.Instance)
        .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<>)))
        .AsImplementedInterfaces()
        .WithScopedLifetime()

        .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
        .AsImplementedInterfaces()
        .WithScopedLifetime()

        .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)))
        .AsImplementedInterfaces()
        .WithScopedLifetime();

});

builder.Services.AddScoped<ICommandHandler<Wms.Work.Application.Tasks.Comand.ReassignWorkerCommand, bool>, Wms.Work.Application.Tasks.Comand.ReassignWorkerCommand.Handler>();

builder.Services.AddScoped<IDispatcher, Dispatcher>();
builder.Services.AddValidatorsFromAssembly(PickListApplicationAssembly.Instance);
builder.Services.AddPickListBackgroundGeneration(builder.Configuration);

builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

app.UseGlobalExceptionHandling();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

PickListEndpoints.MapEndpoint(app);

app.Run();
