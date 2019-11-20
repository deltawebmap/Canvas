using DeltaWebMapCanvas.Entities;
using LibDeltaSystem.Db.System;
using MongoDB.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DeltaWebMapCanvas
{
    public class UserConnection : WebsocketConnection
    {
        /// <summary>
        /// User ID this session is mapped to
        /// </summary>
        public ObjectId user_id;

        /// <summary>
        /// Canvas this is associated with
        /// </summary>
        public LoadedCanvas canvas;

        /// <summary>
        /// User color
        /// </summary>
        public string color;

        /// <summary>
        /// User data
        /// </summary>
        public DbUser user;

        /// <summary>
        /// Token that can be used to pick up if a user ever gets disconnected
        /// </summary>
        public string resume_token;

        /// <summary>
        /// Set to true when we are currently busy subscrbing to a canvas
        /// </summary>
        public bool subscribing;

        /// <summary>
        /// Creates a session.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public static async Task<UserConnection> AuthenticateSession(string token)
        {
            //Auth user
            DbUser u = await Program.conn.AuthenticateUserToken(token);

            //Create a new session
            return new UserConnection
            {
                user_id = u._id,
                color = Program.config.user_colors[Program.rand.Next(0, Program.config.user_colors.Length)],
                user = u
            };
        }

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="data"></param>
        public void SendMessage(WebsocketMessageOpcode op, BaseMessage payload)
        {
            //Convert to bytes first
            byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new WebsocketMessageContainer
            {
                opcode = op,
                payload = payload
            }));

            //Send
            SendMessage(data, System.Net.WebSockets.WebSocketMessageType.Text);
        }

        public override void OnMessageReceived(byte[] data, int length, System.Net.WebSockets.WebSocketMessageType type)
        {
            //If this is a binary message, this is always a request to add lines
            if(type == System.Net.WebSockets.WebSocketMessageType.Binary)
            {
                canvas.AddPoints(data, this);
            } else if (type == System.Net.WebSockets.WebSocketMessageType.Text)
            {
                //Decode
                IncomingMessage msg = JsonConvert.DeserializeObject<IncomingMessage>(Encoding.UTF8.GetString(data, 0, length));

                //Run action
                switch(msg.opcode)
                {
                    case IncomingMessageOpcode.SwitchCanvas: OnCmd_CanvasChange(msg.payload); break;
                    case IncomingMessageOpcode.UnsubscribeCanvas: OnCmd_CanvasUnsubscribe(msg.payload); break;
                    case IncomingMessageOpcode.ClearCanvas: OnCmd_CanvasClear(msg.payload); break;
                    case IncomingMessageOpcode.Ping: OnCmd_Ping(msg.payload); break;
                }                
            }
        }

        private void OnCmd_CanvasChange(JObject payload)
        {
            ChangeSubscribedCanvas(payload.Value<string>("canvas_id")).GetAwaiter().GetResult();
        }

        private void OnCmd_CanvasUnsubscribe(JObject payload)
        {
            //Stop if we're already busy
            if (subscribing)
                return;
            subscribing = true;

            //If we have a set canvas, unsubscribe
            if (canvas != null)
            {
                canvas.Unsubscribe(this).GetAwaiter().GetResult();
                canvas = null;
            }
            subscribing = false;
        }

        private void OnCmd_CanvasClear(JObject payload)
        {
            if (canvas != null)
                canvas.Clear(this);
        }

        private void OnCmd_Ping(JObject payload)
        {
            last_ping = DateTime.UtcNow;
        }

        /// <summary>
        /// Switches the current active canvas
        /// </summary>
        /// <returns></returns>
        public async Task<bool> ChangeSubscribedCanvas(string id)
        {
            //Stop if we're already busy
            if (subscribing)
                return false;
            subscribing = true;

            //If we have a set canvas, unsubscribe
            if (canvas != null)
            {
                await canvas.Unsubscribe(this);
                canvas = null;
            }

            //Now, get the canvas
            if (!ObjectId.TryParse(id, out ObjectId canvas_id))
            {
                subscribing = false;
                return false;
            }
            canvas = await LoadedCanvas.GetCanvas(canvas_id);
            if (canvas == null)
            {
                subscribing = false;
                return false;
            }

            //Subscribe to this canvas
            await canvas.Subscribe(this);

            subscribing = false;
            return true;
        }

        /// <summary>
        /// Called when we close
        /// </summary>
        /// <returns></returns>
        public override async Task OnClosed()
        {
            //Unsubscribe
            if (canvas != null)
                await canvas.Unsubscribe(this);
        }
    }
}
