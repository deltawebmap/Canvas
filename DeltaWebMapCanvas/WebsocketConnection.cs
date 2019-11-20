using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeltaWebMapCanvas
{
    public abstract class WebsocketConnection
    {
        /// <summary>
        /// The socket used.
        /// </summary>
        private WebSocket sock;

        /// <summary>
        /// Task to send data on the wire
        /// </summary>
        private Task sendTask;

        /// <summary>
        /// Last ping that was sent from this client
        /// </summary>
        public DateTime last_ping;

        /// <summary>
        /// Output queue
        /// </summary>
        private ConcurrentQueue<Tuple<byte[], WebSocketMessageType>> queue;

        public WebsocketConnection()
        {
            sendTask = Task.CompletedTask;
            queue = new ConcurrentQueue<Tuple<byte[], WebSocketMessageType>>();
            last_ping = DateTime.UtcNow;
        }

        /// <summary>
        /// Accepts an incoming connection.
        /// </summary>
        /// <param name="sock">Websocket used.</param>
        /// <returns></returns>
        public async Task Run(WebSocket sock)
        {
            this.sock = sock;

            //Flush queue
            Flush();

            //Start loops
            await ReadLoop();

            //Tell the client we closed
            await OnClosed();

            //Shut down the connection
            await Close();
        }

        /// <summary>
        /// Used when data is sent.
        /// </summary>
        /// <param name="data"></param>
        public abstract void OnMessageReceived(byte[] data, int length, WebSocketMessageType type);

        /// <summary>
        /// Used when the socket is closed.
        /// </summary>
        /// <param name="data"></param>
        public abstract Task OnClosed();

        /// <summary>
        /// Sends data to the client.
        /// </summary>
        /// <param name="data"></param>
        public void SendMessage(byte[] data, WebSocketMessageType type)
        {
            sock.SendAsync(data, type, true, CancellationToken.None).GetAwaiter().GetResult();
            //FIX THIS ASAP! This is a janky workaround for a bug that my original queue has
            return;
            
            //Add to queue
            queue.Enqueue(new Tuple<byte[], WebSocketMessageType>(data, type));

            //Flush
            Flush();
        }

        /// <summary>
        /// Flushes new buffer messages. Should be used every time we add to the queue
        /// </summary>
        private void Flush()
        {
            lock (sendTask)
            {
                //Ignore if we're already flushing
                if (!sendTask.IsCompleted)
                    return;

                //Trigger
                sendTask = SendQueuedMessages();
            }
        }

        /// <summary>
        /// Loop for reading from the websocket
        /// </summary>
        /// <returns></returns>
        private async Task ReadLoop()
        {
            try
            {
                var buffer = new byte[Program.config.buffer_size];
                WebSocketReceiveResult result = await sock.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                while (!result.CloseStatus.HasValue)
                {
                    OnMessageReceived(buffer, result.Count, result.MessageType);
                    result = await sock.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                //We might log this in the future.
                Console.WriteLine(ex.Message + ex.StackTrace);
            }
        }

        /// <summary>
        /// Sends any messages queued. Should be used every time we add to the queue
        /// </summary>
        /// <returns></returns>
        private async Task SendQueuedMessages()
        {
            //Make sure that we're still connected
            if (sock.CloseStatus.HasValue)
                return;

            //Lock queue and send all
            Tuple<byte[], WebSocketMessageType> data;
            while (queue.TryDequeue(out data))
            {
                //Send on the network
                try
                {
                    await sock.SendAsync(data.Item1, data.Item2, true, CancellationToken.None);
                }
                catch
                {
                    //Add back, trigger fault
                    queue.Enqueue(data);
                    await Close();
                    return;
                }
            }
        }

        /// <summary>
        /// Ends the connection.
        /// </summary>
        /// <returns></returns>
        public async Task Close()
        {
            //Close
            if (!sock.CloseStatus.HasValue)
            {
                //Signal that this has closed
                try
                {
                    await sock.CloseAsync(WebSocketCloseStatus.Empty, null, CancellationToken.None);
                }
                catch { }
            }
        }
    }
}
