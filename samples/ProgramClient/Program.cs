using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

Console.OutputEncoding=new UTF8Encoding(false);
if(args.Length<2){Console.WriteLine("用法：dotnet run -- <HTTP地址> status|validate|run|step|pause|continue|stop [nowait]\n先在 AxisLab 中保存程序。run/continue/step 默认等待完成或断点暂停；nowait 仅提交。");return 1;}
try
{
    using var http=new HttpClient{BaseAddress=new Uri(args[0].TrimEnd('/')+'/'),Timeout=TimeSpan.FromSeconds(15)};
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(650));var ct=timeout.Token;
    var jsonOptions=new JsonSerializerOptions(JsonSerializerDefaults.Web){MaxDepth=2048};
    async Task<JsonElement> Request(string path,object? body=null)
    {
        using var response=body is null?await http.GetAsync("api/v1/"+path,ct):await http.PostAsJsonAsync("api/v1/"+path,body,ct);
        var json=await response.Content.ReadFromJsonAsync<JsonElement>(jsonOptions,ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException(json.ToString());return json;
    }
    async Task<int> Wait(JsonElement first)
    {
        var run=first.GetProperty("runId").GetString();var session=first.GetProperty("sessionId").GetString();var value=first;
        while(true)
        {
            if(value.GetProperty("runId").GetString()!=run || value.GetProperty("sessionId").GetString()!=session)throw new InvalidOperationException("程序运行或工程会话已改变。");
            var status=value.GetProperty("status").GetString();
            if(status is "Completed" or "Failed" or "Cancelled" or "Paused")
            {
                Console.WriteLine($"Program {status}: run={run}, steps={value.GetProperty("executedBlocks")}");
                Console.WriteLine(JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
                return status=="Completed"?0:status=="Paused"?2:1;
            }
            await Task.Delay(150,ct);value=await Request("program/status");
        }
    }
    var operation=args[1];
    if(operation=="status"){Console.WriteLine(JsonSerializer.Serialize(await Request("program/status"),new JsonSerializerOptions{WriteIndented=true}));return 0;}
    if(operation=="validate"){var validation=await Request("program/validate",new{});Console.WriteLine(validation);return validation.GetProperty("valid").GetBoolean()?0:2;}
    JsonElement result;
    if(operation is "run" or "step")
    {
        var current=await Request("program/status");
        if(operation=="step" && current.GetProperty("status").GetString()=="Paused")result=await Request("program/step",new{});
        else
        {
            var project=await Request("project");result=await Request("program/run",new{expectedRevision=project.GetProperty("revision").GetInt64(),singleStep=operation=="step"});
        }
    }
    else if(operation is "pause" or "continue" or "stop")result=await Request("program/"+operation,new{});
    else throw new ArgumentException("不支持此程序操作。");
    if(operation is "pause" or "stop" || args.Length>2 && args[2]=="nowait"){Console.WriteLine(result);return 0;}
    return await Wait(result);
}
catch(Exception e) when(e is HttpRequestException or InvalidOperationException or OperationCanceledException or ArgumentException or JsonException)
{Console.Error.WriteLine(e.Message);return 1;}
