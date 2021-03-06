// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Clockwise;
using Microsoft.DotNet.Try.Jupyter.Protocol;
using Microsoft.DotNet.Try.Protocol;
using Newtonsoft.Json.Linq;
using WorkspaceServer;
using WorkspaceServer.Servers.Roslyn;
using Buffer = Microsoft.DotNet.Try.Protocol.Buffer;

namespace Microsoft.DotNet.Try.Jupyter
{
    public class JupyterRequestContextHandler : ICommandHandler<JupyterRequestContext>
    {
        private readonly PackageRegistry _packageRegistry;
        private int _executionCount;

        public JupyterRequestContextHandler(PackageRegistry packageRegistry)
        {
            _packageRegistry = packageRegistry ?? throw new ArgumentNullException(nameof(packageRegistry));
        }

        public async Task<ICommandDeliveryResult> Handle(
            ICommandDelivery<JupyterRequestContext> delivery)
        {
            switch (delivery.Command.Request.Header.MessageType)
            {
                case MessageTypeValues.ExecuteRequest:
                    var transient = new Dictionary<string, object> { { "display_id", Guid.NewGuid().ToString() } };

                    var jObject = (JObject)delivery.Command.Request.Content;
                    var executeRequest = jObject.ToObject<ExecuteRequest>();

                    var code = executeRequest.Code;

                    var workspace = new Workspace(
                        files: new[]
                               {
                                   new File("Program.cs", Scaffold())
                               },
                        buffers:new[]
                                {
                                    new Buffer(new BufferId("Program.cs", "main"), code), 
                                },
                        workspaceType: "console");

                    var workspaceRequest = new WorkspaceRequest(workspace);

                    var server = new RoslynWorkspaceServer(new PackageRegistry());

                    var result = await server.Run(workspaceRequest);

                    var messageBuilder = delivery.Command.Builder;
                    var ioPubChannel = delivery.Command.IoPubChannel;
                    var serverChannel = delivery.Command.ServerChannel;

                    if (!executeRequest.Silent)
                    {
                        _executionCount++;

                        var executeInput = messageBuilder.CreateMessage(
                            MessageTypeValues.ExecuteInput,
                            new ExecuteInput
                            {
                                Code = code,
                                ExecutionCount = _executionCount
                            },
                            delivery.Command.Request.Header);

                        ioPubChannel.Send(executeInput);
                    }

                    // execute result
                    var output = string.Join("\n", result.Output);

                    
                    // executeResult data
                    var executeResultData = new ExecuteResult()
                    {
                        Data = new JObject
                        {
                            { "text/html", output },
                            { "text/plain", output }
                        },
                        Transient = transient,
                        ExecutionCount = _executionCount
                    };



                    var resultSucceeded = result.Succeeded &&
                                          result.Exception == null;

                    if (resultSucceeded)
                    {
                        // reply ok
                        var executeReplyPayload = new ExecuteReplyOk
                        {
                            ExecutionCount = _executionCount
                        };

                        // send to server
                        var executeReply = messageBuilder.CreateMessage(
                            MessageTypeValues.ExecuteReply, 
                            executeReplyPayload, 
                            delivery.Command.Request.Header);

                        executeReply.Identifiers = delivery.Command.Request.Identifiers;

                        serverChannel.Send(executeReply);
                    }
                    else
                    {
                        var errorContent = new Error
                        {
                            EName = string.IsNullOrWhiteSpace(result.Exception)
                                        ? "Compile Error" 
                                        : "Unhandled Exception",
                            EValue = output,
                            Traceback = new List<string>()
                        };

                        //  reply Error
                        var executeReplyPayload = new ExecuteReplyError(errorContent)
                        {
                            ExecutionCount = _executionCount
                        };

                        // send to server
                        var executeReply = messageBuilder.CreateMessage(
                            MessageTypeValues.ExecuteReply, 
                            executeReplyPayload, 
                            delivery.Command.Request.Header);

                        executeReply.Identifiers = delivery.Command.Request.Identifiers;

                        serverChannel.Send(executeReply);

                        if (!executeRequest.Silent)
                        {
                            // send on io
                            var error = messageBuilder.CreateMessage(
                                MessageTypeValues.Error, 
                                errorContent, 
                                delivery.Command.Request.Header);
                            ioPubChannel.Send(error);

                            // send on stderr
                            var stdErr = new StdErrStream
                            {
                                Text = errorContent.EValue
                            };
                            var stream = messageBuilder.CreateMessage(
                                MessageTypeValues.Stream, 
                                stdErr, 
                                delivery.Command.Request.Header);
                            ioPubChannel.Send(stream);
                        }
                    }

                    if (!executeRequest.Silent && resultSucceeded)
                    {
                        // send on io
                        var executeResultMessage = messageBuilder.CreateMessage(
                            MessageTypeValues.ExecuteResult,
                            executeResultData,
                            delivery.Command.Request.Header);
                        ioPubChannel.Send(executeResultMessage);
                    }

                    break;
            }

            return delivery.Complete();
        }

        private static string Scaffold() =>
            @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class Program
{
    public static void Main()
    {
#region main
#endregion
    }
}
";
    }
}