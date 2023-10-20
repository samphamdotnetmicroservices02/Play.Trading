using System;
using System.Reflection;
using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Play.Common.HealthChecks;
using Play.Common.Identity;
using Play.Common.Logging;
using Play.Common.MassTransit;
using Play.Common.MongoDB;
using Play.Common.OpenTelemetry;
using Play.Common.Settings;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Service.Entities;
using Play.Trading.Service.Exceptions;
using Play.Trading.Service.Settings;
using Play.Trading.Service.SignalR;
using Play.Trading.Service.StateMachines;

namespace Play.Trading.Service
{
    public class Startup
    {
        private const string AllowedOriginSetting = "AllowedOrigin";
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMongo()
                .AddMongoRepository<CatalogItem>("catalogitems")
                .AddMongoRepository<ApplicationUser>("users")
                .AddMongoRepository<InventoryItem>("inventoryitems")
                .AddJwtBearerAuthentication();

            AddMassTransit(services);

            services.AddControllers(options =>
            {
                options.SuppressAsyncSuffixInActionNames = false;
            })
            .AddJsonOptions(options =>
            {
                /*
                * If there's any null in return properties of the Dto, those are going to be ignored
                */
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Play.Trading.Service", Version = "v1" });
            });

            services.AddSingleton<IUserIdProvider, UserIdProvider>()
                .AddSingleton<MessageHub>()
                .AddSignalR();

            services.AddHealthChecks()
                .AddMongoDb();

            services.AddSeqLogging(Configuration)
                .AddTracing(Configuration);

            /*
            * add this for microservice to export the metrics into Prometheus, which is our tool or server that is going to be
            * collecting that information so that we can see later on in a very nice way.
            */
            services.AddOpenTelemetry().WithMetrics(builder => 
            {
                var settings = Configuration.GetSection(nameof(ServiceSettings)).Get<ServiceSettings>();

                // the name if this Meter should be matched the name of the Meter you specify in PurchaseStateMachine, which is
                // "Meter meter = new(settings.ServiceName);" in constructor
                builder.AddMeter(settings.ServiceName)

                    //capture the metrics of HttpClient and and AspNetCore 
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    
                    //tell OpenTelemetry that we want to export these metrics into a Prometheus.
                    .AddPrometheusExporter();
            });

            
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Play.Trading.Service v1"));

                app.UseCors(builder =>
                {
                    builder.WithOrigins(Configuration[AllowedOriginSetting])
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        //we need to AllowCredentials because they have to allow in order for cookie-based sticky sessions
                        // to work correctly. This is a requirement of SignalR regardless of if you use authentication or not.
                        // You always have to allow credentials here. We didn't need these in all microservices, but we need this
                        // one here to use SignalR.
                        .AllowCredentials();
                });
            }

            /*
            * Prometheus:
            * With the metrics side, we also need to do one more thing and that is to enable or create or expose what's going to
            * be called the scraping endpoint. So this is the endpoint that tools like Prometheus can use in a giving interval,
            * start pulling down and pulling into Prometheus, the metrics that we've been collecting across the lifetime of the
            * application. This "UseOpenTelemetryPrometheusScrapingEndpoint" is going to stand up that endpoint that it actually
            * ends with /metrics. You can configure it if you want to, for us, that's going to be good enough.
            */
            app.UseOpenTelemetryPrometheusScrapingEndpoint();

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthentication();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                endpoints.MapHub<MessageHub>("/messagehub");

                endpoints.MapPlayEconomyHealthCheck();
            });
        }

        private void AddMassTransit(IServiceCollection services)
        {
            services.AddMassTransit(configure =>
            {
                //because we add some specific configurations for Saga, so we use this instead
                configure.UsingPlayEconomyMessageBroker(Configuration, retryConfigurator =>
                {
                    retryConfigurator.Interval(3, TimeSpan.FromSeconds(5));
                    retryConfigurator.Ignore<UnknownItemException>();
                });
                configure.AddConsumers(Assembly.GetEntryAssembly());
                configure.AddSagaStateMachine<PurchaseStateMachine, PurchaseState>((x, sagaConfigurator) =>
                {
                    /*
                    * Because we send message to consumer before persisting Accepted into database, and then the consumer send back and now we are 
                    * not in Accepted yet, so we consume that message but the operation just perform when we are in Accepted state, so we don't do
                    * anything when we receive the message back. By configure .UseInMemoryOutbox(), we can solve that problem. It means no message
                    * will be sent from one of the saga pipelines until we persist its state into database.
                    */
                    //x.UseInMemoryOutbox().(configurator => configurator.UseInMemoryOutbox());
                    sagaConfigurator.UseInMemoryOutbox();
                })

                    .MongoDbRepository(r =>
                    {
                        var serviceSettings = Configuration.GetSection(nameof(ServiceSettings)).Get<ServiceSettings>();
                        var mongoDbSettings = Configuration.GetSection(nameof(MongoDbSettings)).Get<MongoDbSettings>();

                        r.Connection = mongoDbSettings.ConnectionString;
                        r.DatabaseName = serviceSettings.ServiceName;
                    });
            });

            var queueSettings = Configuration.GetSection(nameof(QueueSettings)).Get<QueueSettings>();

            EndpointConvention.Map<GrantItems>(new Uri(queueSettings.GrantItemsQueueAddress));
            EndpointConvention.Map<DebitGil>(new Uri(queueSettings.DebitGilQueueAddress));
            EndpointConvention.Map<SubtractItems>(new Uri(queueSettings.SubtractItemsQueueAddress));
        }
    }
}
