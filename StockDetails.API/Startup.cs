using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Runtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Prometheus;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.AwsCloudWatch;
using StockDetails.API.Logging;
using StockDetails.API.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace StockDetails.API
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            SetUpLogger();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var Region = Configuration["AWSCognito:Region"];
            var PoolId = Configuration["AWSCognito:PoolId"];
            var AppClientId = Configuration["AWSCognito:AppClientId"];

            #region DynamoDB
            //var AWSDynamoDBAccessKey = Configuration["AWSDynamoDB:AccessKey"];
            //var AWSDynamoDBSecretKey = Configuration["AWSDynamoDB:SecretKey"];
            //var AWSDynamoDBServiceURL = Configuration["AWSDynamoDB:ServiceURL"];

            //var credentials = new BasicAWSCredentials(AWSDynamoDBAccessKey, AWSDynamoDBSecretKey);
            //var config = new AmazonDynamoDBConfig()
            //{
            //    RegionEndpoint = RegionEndpoint.USEast2,
            //    ServiceURL = AWSDynamoDBServiceURL
            //};
            //var client = new AmazonDynamoDBClient(credentials, config);
            //services.AddSingleton<IAmazonDynamoDB>(client);
            //services.AddSingleton<IDynamoDBContext, DynamoDBContext>();
            #endregion

            Action<JwtBearerOptions> options = o =>
            {
                o.RequireHttpsMetadata = false;
                o.SaveToken = true;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (s, securityToken, identifier, parameters) =>
                    {
                        // Get JsonWebKeySet from AWS
                        var json = new WebClient().DownloadString(parameters.ValidIssuer + "/.well-known/jwks.json");
                        // Serialize the result
                        return JsonConvert.DeserializeObject<JsonWebKeySet>(json).Keys;
                    },
                    ValidateIssuer = true,
                    ValidIssuer = $"https://cognito-idp.{Region}.amazonaws.com/{PoolId}",
                    ValidateLifetime = true,
                    LifetimeValidator = (before, expires, token, param) => expires > DateTime.UtcNow,
                    ValidateAudience = false,
                    RequireExpirationTime = true
                };
            };
            services.AddVersionedApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Stock Details API", Version = "v1" });
                c.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, new OpenApiSecurityScheme()
                {
                    Description = @"JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token in the text input below. Example: 'Bearer 12345abcdef'",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = JwtBearerDefaults.AuthenticationScheme,
                    BearerFormat = "JWT"
                });
                c.AddSecurityRequirement(new OpenApiSecurityRequirement()
                          {
                              {
                                  new OpenApiSecurityScheme
                                  {
                                      Reference = new OpenApiReference
                                      {
                                          Type = ReferenceType.SecurityScheme,
                                          Id = JwtBearerDefaults.AuthenticationScheme
                                      },
                                      Scheme = "oauth2",
                                      Name = JwtBearerDefaults.AuthenticationScheme,
                                      In = ParameterLocation.Header
                                  },
                                  new string[] {}
                              }
                          });
            });
            services.AddApiVersioning();

            services.Configure<StockDatabaseSettings>(
              Configuration.GetSection(nameof(StockDatabaseSettings)));

            services.AddSingleton<IStockDatabaseSettings>(sp =>
                sp.GetRequiredService<IOptions<StockDatabaseSettings>>().Value);

            services.AddSingleton<IStockService, StockService>();

            //services.AddSingleton<IDynamoDBService, DynamoDBService>();

            services.AddControllers();

            services.AddHttpContextAccessor();

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            StockDetailsLogEnricher.ServiceProvider = app.ApplicationServices;
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseAuthorization();

            app.UseAuthentication();

            app.UseCors();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            app.UseSwagger();

            app.UseMetricServer();

            app.UseHttpMetrics();

            app.UseRequestMiddleware();

            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Stock Details API V1");
                c.RoutePrefix = "";
            });
        }

        private void SetUpLogger()
        {
            var logLevel = LogEventLevel.Information;
            var retentionPolicy = LogGroupRetentionPolicy.ThreeDays;
            var region = RegionEndpoint.GetBySystemName(Configuration.GetSection("AWSCred").GetSection("Region").Value);
            var levelSwitch = new LoggingLevelSwitch();
            levelSwitch.MinimumLevel = logLevel;
            var formatter = new CustomLogFormatter();
            var options = new CloudWatchSinkOptions
            {
                LogGroupName = "company-logs",
                TextFormatter = formatter,
                MinimumLogEventLevel = logLevel,
                BatchSizeLimit = 100,
                QueueSizeLimit = 10000,
                Period = TimeSpan.FromSeconds(10),
                CreateLogGroup = true,
                LogStreamNameProvider = new DefaultLogStreamProvider(),
                RetryAttempts = 5,
                LogGroupRetentionPolicy = retentionPolicy
            };
            var AccessKey = Configuration.GetSection("AWSCred").GetSection("AccessKey").Value;
            var SecretKey = Configuration.GetSection("AWSCred").GetSection("SecretKey").Value;

            var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
            var client = new AmazonCloudWatchLogsClient(credentials, region);

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Logger(l1 => l1
                    .MinimumLevel.ControlledBy(levelSwitch)
                    .WriteTo.AmazonCloudWatch(options, client))
              .CreateLogger();

        }
    }

    public class CustomLogFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            output.Write("Timestamp - {0} | Level - {1} | Message {2} {3} {4}", logEvent.Timestamp, logEvent.Level, logEvent.MessageTemplate, JsonConvert.SerializeObject(logEvent.Properties), output.NewLine);
            if (logEvent.Exception != null)
            {
                output.Write("Exception - {0}", logEvent.Exception);
            }
        }
    }
}
