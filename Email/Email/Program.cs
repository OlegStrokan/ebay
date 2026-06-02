using Email.Messaging;
using Email.Options;
using Email.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<EmailDeliveryOptions>(builder.Configuration.GetSection(EmailDeliveryOptions.SectionName));

builder.Services.AddSingleton<IProcessedMessageStore, PostgresProcessedMessageStore>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<KafkaEmailConsumer>();
builder.Services.AddHostedService<KafkaEmailDlqReplayWorker>();
builder.Services.AddHostedService<ProcessedMessageCleanupWorker>();

var host = builder.Build();
host.Run();
