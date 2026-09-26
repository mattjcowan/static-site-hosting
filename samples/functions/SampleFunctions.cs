#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package ScottPlot@5.1.59

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using System.Collections;
using System.Reflection;
using System.Text.Json;

#if LINQPAD
async Task Main()
{
    var context = new DefaultHttpContext();
    context.Request.Method = "POST";
    context.Request.Path = "/ping/abcdef";

    (await Handlers.PingPostAsync(context, "abcdef")).Dump();
    
    // Prints: 200

    var drawContext = new DefaultHttpContext();
    drawContext.Request.Method = "POST";
    drawContext.Request.Path = "/draw/Hello from ScottPlot!";

    var result = await Handlers.DrawTextPostAsync(drawContext, "Hello from ScottPlot!");
    if (result is FileContentHttpResult file)
        Util.Image(file.FileContents.ToArray()).Dump(file.ContentType);
}
#endif

public static class Handlers
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static IResult ToResult(object response, int statusCode = 200) =>
        Results.Json(response, options: JsonOptions, contentType: "application/json", statusCode: statusCode);

    /// <summary>POST /ping or POST /ping/Jane</summary>
    [HttpPost("/ping/{name?}")]
    public static Task<IResult> PingPostAsync(HttpContext context, string? name)
    {
        var greeting = string.IsNullOrWhiteSpace(name) ? "Hi" : $"Hi {name.Trim()}";
        var response = new {result = new { pong = greeting } };
        return Task.FromResult(ToResult(response));
    }


    [HttpPost("/draw/{text}")]
    public static Task<IResult> DrawTextPostAsync(HttpContext context, string text)
    {
        var plot = new Plot();
        var label = plot.Add.Text(text, 0, 0);
        label.LabelFontSize = 32;
        label.LabelAlignment = Alignment.MiddleCenter;
        plot.HideGrid();
        plot.Layout.Frameless();

        byte[] png = plot.GetImageBytes(600, 200, ImageFormat.Png);
        return Task.FromResult(Results.File(png, "image/png"));
    }
}