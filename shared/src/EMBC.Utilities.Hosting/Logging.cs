﻿using System;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Exceptions;

namespace EMBC.Utilities.Hosting
{
    internal static class Logging
    {
        public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3} {SourceContext}] {Message:lj}{NewLine}{Exception}";

        public static void ConfigureSerilog(HostBuilderContext hostBuilderContext, LoggerConfiguration loggerConfiguration, string appName)
        {
            loggerConfiguration
                .ReadFrom.Configuration(hostBuilderContext.Configuration)
                .Enrich.WithMachineName()
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .Enrich.WithProperty("app", appName)
                .Enrich.WithEnvironmentName()
                .Enrich.WithEnvironmentUserName()
                .Enrich.WithCorrelationId()
                .Enrich.WithCorrelationIdHeader()
                .Enrich.WithClientAgent()
                .Enrich.WithClientIp()
                .Enrich.WithSpan()
                .WriteTo.Console(outputTemplate: LogOutputTemplate)
#if DEBUG
            //.WriteTo.File($"./{appName}_errors.log", LogEventLevel.Error)
#endif
            ;

            var splunkUrl = hostBuilderContext.Configuration.GetValue("SPLUNK_URL", string.Empty);
            var splunkToken = hostBuilderContext.Configuration.GetValue("SPLUNK_TOKEN", string.Empty);
            if (string.IsNullOrWhiteSpace(splunkToken) || string.IsNullOrWhiteSpace(splunkUrl))
            {
                Log.Warning($"Logs will NOT be forwarded to Splunk: check SPLUNK_TOKEN and SPLUNK_URL env vars");
            }
            else
            {
                loggerConfiguration
                    .WriteTo.EventCollector(
                        splunkHost: splunkUrl,
                        eventCollectorToken: splunkToken,
                        messageHandler: new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        },
                        renderTemplate: false);
                Log.Information($"Logs will be forwarded to Splunk");
            }
        }

        public static IApplicationBuilder SetDefaultRequestLogging(this IApplicationBuilder applicationBuilder)
        {
            applicationBuilder.UseSerilogRequestLogging(opts =>
            {
                opts.IncludeQueryInRequestPath = true;
                opts.GetLevel = ExcludeHealthChecks;
                opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
                {
                    diagCtx.Set("User", httpCtx.User.Identity?.Name ?? string.Empty);
                    diagCtx.Set("Host", httpCtx.Request.Host);
                    diagCtx.Set("ContentLength", httpCtx.Response.ContentLength?.ToString() ?? string.Empty);
                };
            });

            return applicationBuilder;
        }

        private static LogEventLevel ExcludeHealthChecks(HttpContext ctx, double _, Exception ex) =>
              ex != null
                  ? LogEventLevel.Error
                  : ctx.Response.StatusCode >= (int)HttpStatusCode.InternalServerError
                      ? LogEventLevel.Error
                      : ctx.Request.Path.StartsWithSegments("/hc", StringComparison.InvariantCultureIgnoreCase)
                          ? LogEventLevel.Verbose
                          : LogEventLevel.Information;

        public static IServiceCollection AddOpenTelemetry(this IServiceCollection services, string appName)
        {
            services.AddOpenTelemetryTracing(builder =>
            {
                builder
                    .AddConsoleExporter()
                    .AddSource(appName)
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName: appName, serviceVersion: "1.0.0.0")).AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddGrpcCoreInstrumentation()
                    .AddGrpcClientInstrumentation()
                    .AddRedisInstrumentation();
            });
            services.AddSingleton(TracerProvider.Default.GetTracer(appName));

            return services;
        }
    }
}