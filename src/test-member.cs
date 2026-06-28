#!/usr/bin/env -S dotnet --

#:package OpenAI@*
#:package System.ClientModel@*

using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

// 从环境变量读取
var endpoint = Environment.GetEnvironmentVariable("ENDPOINT");
var apiKey = Environment.GetEnvironmentVariable("APIKEY");  
var model = Environment.GetEnvironmentVariable("MODEL") ?? "deepseek-v4-flash";

if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("请设置 ENDPOINT 和 APIKEY 环境变量");
    return;
}

Console.WriteLine($"Endpoint: {endpoint}");
Console.WriteLine($"Model: {model}");
Console.WriteLine();

var memberText = @"<member name=""M:YamlDotNet.Serialization.YamlSerializableAttribute.#ctor(System.Type)"">
            <summary>
            Use this constructor if the attribute is placed on the <see cref=""T:YamlDotNet.Serialization.StaticContext"" />.
            </summary>
            <param name=""serializableType"">The type for which to include static code generation.</param>
        </member>";

Console.WriteLine("原文:");
Console.WriteLine(memberText);
Console.WriteLine();

var chat = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions() { Endpoint = new Uri(endpoint) });

var sysmsg = ChatMessage.CreateSystemMessage("""
    你会收到一个c#的xml文档注释。
    你需要翻译为地区/语言代码为："zh-Hans" 所使用的语言，并且保持XML格式不变。
    你的输出必须是可以XElement.Parse(resert)的字符串。
    你被用于API调用进行翻译工作。除了要求你的输出以外不要输出任何对话内容，你的对话内容不会被展示。
    """);

var sw = System.Diagnostics.Stopwatch.StartNew();

try
{
    var completion = await chat.CompleteChatAsync([sysmsg, ChatMessage.CreateUserMessage(memberText)]);
    sw.Stop();

    var usage = completion.Value.Usage;
    Console.WriteLine($"响应时间: {sw.Elapsed.TotalSeconds:F2} 秒");
    Console.WriteLine($"Token: 输入 {usage?.InputTokenCount}, 输出 {usage?.OutputTokenCount}");
    Console.WriteLine();
    Console.WriteLine("翻译结果:");
    Console.WriteLine(completion.Value.Content[0].Text);
}
catch (Exception ex)
{
    sw.Stop();
    Console.WriteLine($"失败 ({sw.Elapsed.TotalSeconds:F2} 秒):");
    Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
}
