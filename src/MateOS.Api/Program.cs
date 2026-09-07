using MateOS.Core.Interfaces;
using MateOS.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Register MateOS services
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IChannelService, ChannelService>();
builder.Services.AddSingleton<IMessageService, MessageService>();
builder.Services.AddSingleton<IMemoryService, MemoryService>();
builder.Services.AddSingleton<IContextService, ContextService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
