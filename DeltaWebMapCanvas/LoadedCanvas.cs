using DeltaWebMapCanvas.Entities;
using LibDeltaSystem.Db.System;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace DeltaWebMapCanvas
{
    public class LoadedCanvas
    {
        /// <summary>
        /// The ID of this canvas
        /// </summary>
        public ObjectId canvas_id;

        /// <summary>
        /// Stores binary canvas data
        /// </summary>
        public Stream canvas_data;

        /// <summary>
        /// Returns the number of lines added
        /// </summary>
        public int line_count => (int)canvas_data.Length / MESSAGE_SIZE;

        /// <summary>
        /// When there is pending data, this is set to true
        /// </summary>
        public bool data_unsaved;

        /// <summary>
        /// User IDs, mapped to the table
        /// </summary>
        public ObjectId[] users;

        /// <summary>
        /// The index where we should write the next user
        /// </summary>
        public int saved_users_index;

        /// <summary>
        /// The size of all messages
        /// </summary>
        public const int MESSAGE_SIZE = 12;

        /// <summary>
        /// Connections that are subscribed to events for this canvas
        /// </summary>
        public List<UserConnection> subscribers;

        /// <summary>
        /// Tokens that can be used to fetch only new data since a client was disconnected.
        /// </summary>
        public Dictionary<string, int> resume_tokens;

        /// <summary>
        /// Last time this was edited
        /// </summary>
        public DateTime last_edit;

        /// <summary>
        /// Last editor
        /// </summary>
        public ObjectId last_editor;

        /// <summary>
        /// Saves the canvas every once in a while
        /// </summary>
        public Timer save_timer;

        /// <summary>
        /// Task for this to load
        /// </summary>
        public Task<bool> load_task;

        /// <summary>
        /// Gets a canvas from the database or from memory
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static async Task<LoadedCanvas> GetCanvas(ObjectId id)
        {
            LoadedCanvas canvas;
            lock (Program.canvases)
            {
                //Check if we are loaded into memory
                if (Program.canvases.ContainsKey(id.ToString()))
                {
                    canvas = Program.canvases[id.ToString()];
                } else
                {
                    //Create canvas object
                    canvas = new LoadedCanvas
                    {
                        canvas_data = new MemoryStream(),
                        data_unsaved = true,
                        subscribers = new List<UserConnection>(),
                        resume_tokens = new Dictionary<string, int>(),
                        canvas_id = id,
                        save_timer = new Timer(60 * 1000)
                    };

                    //Add
                    Program.canvases.Add(id.ToString(), canvas);

                    //Compute
                    canvas.load_task = canvas.InternalGetCanvas(id);
                }
            }

            //Wait for completion
            bool loaded = await canvas.load_task;

            //If loaded, return canvas
            if (!loaded)
                return null;
            else
                return canvas;
        }

        /// <summary>
        /// Creates a canvas
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private async Task<bool> InternalGetCanvas(ObjectId id)
        {
            //Load from the database
            DbCanvas data = await Program.conn.LoadCanvasData(id);
            if (data == null)
                return false;

            //Set data
            saved_users_index = data.user_index;
            users = data.users;
            last_edit = data.last_edited;
            last_editor = data.last_editor;

            //Load data from disk
            if (File.Exists(GetSavePath()))
            {
                using (FileStream fs = new FileStream(GetSavePath(), FileMode.Open))
                    await fs.CopyToAsync(canvas_data);
            }

            //Set timer
            save_timer.AutoReset = true;
            save_timer.Elapsed += DoAutosave;
            save_timer.Start();

            return true;
        }

        /// <summary>
        /// Called when we autosave
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DoAutosave(object sender, ElapsedEventArgs e)
        {
            Save().GetAwaiter().GetResult();
            //TODO: Handle errors here
        }

        /// <summary>
        /// Subscribe a client to this canvas
        /// </summary>
        /// <param name="conn"></param>
        public async Task Subscribe(UserConnection conn)
        {
            //Check resume token, if it is set
            if(conn.resume_token != null && !resume_tokens.ContainsKey(conn.resume_token))
                conn.resume_token = null;

            //Generate resume token
            if (conn.resume_token == null)
            {
                conn.resume_token = LibDeltaSystem.Tools.SecureStringTool.GenerateSecureString(16);
                resume_tokens.Add(conn.resume_token, 0);
            }

            //Send content
            await SendCanvasData(conn, resume_tokens[conn.resume_token]);
            
            //Add here
            if (!subscribers.Contains(conn))
                subscribers.Add(conn);

            //Set as active canvas
            conn.canvas = this;

            //Get user ID index
            byte index = GetUserIDIndex(conn.user_id);

            //Send user info to clients
            SendMessageToSubscribers(WebsocketMessageOpcode.DefineUserID, new DefineUserIDMessage
            {
                icon = conn.user.profile_image_url,
                id = conn.user.id,
                index = index,
                name = conn.user.screen_name,
                color = conn.color
            });
        }

        /// <summary>
        /// Unsubscribes a client from this canvas. Unloads the canvas and writes it to the database if there are no more subscribers
        /// </summary>
        /// <param name="conn"></param>
        /// <returns></returns>
        public async Task Unsubscribe(UserConnection conn)
        {
            //Remove subscription
            if (subscribers.Contains(conn))
                subscribers.Remove(conn);

            //Check if there are any more subscribers
            if(subscribers.Count == 0)
            {
                await End();
            }
        }

        /// <summary>
        /// Unloads the canvas
        /// </summary>
        /// <returns></returns>
        public async Task End()
        {
            //Remove from list of canvases
            Program.canvases.Remove(canvas_id.ToString());

            //Save to the database. This can cause data loss if this canvas is loaded after it is removed but before it is saved!
            await Save();

            //Stop save timer
            save_timer.Stop();
            save_timer.Dispose();

            //Close all resources
            canvas_data.Close();
            canvas_data.Dispose();
        }

        /// <summary>
        /// Saves this canvas to the database.
        /// </summary>
        /// <returns></returns>
        public async Task Save()
        {
            //Stop save timer
            save_timer.Stop();
            
            //Write update for MongoDB
            var updateBuilder = Builders<DbCanvas>.Update;
            var update = updateBuilder.Set("users", users).Set("user_index", saved_users_index).Set("last_editor", last_editor).Set("last_edited", last_edit);
            var filterBuilder = Builders<DbCanvas>.Filter;
            var filter = filterBuilder.Eq("_id", canvas_id);
            await Program.conn.system_canvases.UpdateOneAsync(filter, update);

            //Now, write this to a file
            lock(canvas_data)
            {
                canvas_data.Position = 0;
                using (FileStream fs = new FileStream(GetSavePath(), FileMode.Create))
                {
                    canvas_data.CopyTo(fs);
                }
            }

            //Restart save timer
            save_timer.Start();
        }

        /// <summary>
        /// Clears the canvas and resets it
        /// </summary>
        public void Clear(UserConnection user)
        {
            //Notify clients
            SendMessageToSubscribers(WebsocketMessageOpcode.ClearCanvas, new CanvasClearMessage
            {
                cleared_by_icon = user.user.profile_image_url,
                cleared_by_name = user.user.screen_name
            });

            //Clear data stream
            lock (canvas_data)
            {
                canvas_data.Close();
                canvas_data.Dispose();
                canvas_data = new MemoryStream();
            }

            //Set dirty flag
            data_unsaved = true;
        }

        /// <summary>
        /// Returns the saved filename of this
        /// </summary>
        /// <returns></returns>
        public string GetSavePath()
        {
            return Program.config.map_directory + canvas_id.ToString() + ".deltacanvas";
        }

        /// <summary>
        /// Called whenever a client sends a ping request
        /// </summary>
        /// <param name="user"></param>
        public void OnClientPing(UserConnection user)
        {
            //Set client last ping
            user.last_ping = DateTime.UtcNow;

            //Update token value
            resume_tokens[user.resume_token] = line_count;
        }

        /// <summary>
        /// Processes incoming data from clients. First byte of the array is used as a count.
        /// </summary>
        /// <param name="payload"></param>
        public void AddPoints(byte[] payload, UserConnection user)
        {
            //Get user ID index
            byte index = GetUserIDIndex(user.user_id);

            //Set all user IDs
            for(int i = 0; i<payload[0]; i+=1)
            {
                //Set user ID
                payload[1 + (i * MESSAGE_SIZE)] = index;
            }

            //Write content
            lock (canvas_data)
            {
                canvas_data.Position = canvas_data.Length;
                canvas_data.Write(payload, 1, payload[0] * MESSAGE_SIZE);
            }

            //Set dirty flag
            data_unsaved = true;

            //Set last edit
            last_edit = DateTime.UtcNow;
            last_editor = user.user_id;

            //Now, send this data to all of our subscribers excluding ourself
            foreach(var s in subscribers)
            {
                //Do not match ourselves
                if (s == user)
                    continue;

                //Send
                s.SendMessage(payload, System.Net.WebSockets.WebSocketMessageType.Binary);
            }
        }

        /// <summary>
        /// Returns the user index, or creates one if it doesn't exist
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public byte GetUserIDIndex(ObjectId id)
        {
            int index = Array.IndexOf(users, id);
            if (index != -1)
                return (byte)index;

            //We'll need to add it
            users[saved_users_index] = id;
            saved_users_index++;

            //Return it
            return (byte)(saved_users_index - 1);
        }

        /// <summary>
        /// Sends a message to all subscribers
        /// </summary>
        /// <param name="data"></param>
        public void SendMessageToSubscribers(byte[] payload, System.Net.WebSockets.WebSocketMessageType type)
        {
            foreach (var s in subscribers)
            {
                //Send
                s.SendMessage(payload, type);
            }
        }

        /// <summary>
        /// Sends a message to all subscribers
        /// </summary>
        /// <param name="data"></param>
        public void SendMessageToSubscribers(WebsocketMessageOpcode op, BaseMessage payload)
        {
            //Convert to bytes first
            byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new WebsocketMessageContainer
            {
                opcode = op,
                payload = payload
            }));

            //Send
            foreach (var s in subscribers)
            {
                //Send
                s.SendMessage(data, System.Net.WebSockets.WebSocketMessageType.Text);
            }
        }

        /// <summary>
        /// Called to inform clients about the color change of another client
        /// </summary>
        /// <param name="conn"></param>
        public void SetClientColor(UserConnection conn)
        {
            SendMessageToSubscribers(WebsocketMessageOpcode.DefineUserColor, new DefineUserColorMessage
            {
                id = conn.user_id.ToString(),
                color = conn.color
            });
        }

        /// <summary>
        /// Sends all saved canvas data to a client
        /// </summary>
        /// <param name="conn"></param>
        /// <returns></returns>
        public async Task SendCanvasData(UserConnection conn, int start)
        {
            //Send all users
            for(int i = 0; i<saved_users_index; i++)
            {
                //Get user data
                DbUser user = await Program.conn.GetUserByIdAsync(users[i].ToString());

                //Try and get the color
                string color = "#FFFFFF";
                var sub = subscribers.Where(x => x.user_id.ToString() == users[i].ToString()).FirstOrDefault();
                if (sub != null)
                    color = sub.color;

                //Create data
                DefineUserIDMessage msg;
                if(user == null)
                {
                    msg = new DefineUserIDMessage
                    {
                        icon = null,
                        id = users[i].ToString(),
                        index = i,
                        name = "DELETED_USER_" + users[i],
                        color = color
                    };
                } else
                {
                    msg = new DefineUserIDMessage
                    {
                        icon = user.profile_image_url,
                        id = user.id,
                        index = i,
                        name = user.screen_name,
                        color = color
                    };
                }

                //Send to all
                conn.SendMessage(WebsocketMessageOpcode.DefineUserID, msg);
            }

            try
            {
                //Now, send chunks of map content
                Console.WriteLine("locking");
                lock (canvas_data)
                {
                    Console.WriteLine("locked");
                    int count = 255;
                    byte[] buffer = new byte[(MESSAGE_SIZE * count) + 1];
                    canvas_data.Position = 0;
                    int index = 0;
                    while(true)
                    {
                        //Determine remaining parts
                        int remaining = Math.Min((line_count - index), count);

                        //Break if out
                        if (remaining <= 0)
                            break;
                        
                        //Put parts in buffer
                        canvas_data.Read(buffer, 1, MESSAGE_SIZE * remaining);
                        buffer[0] = (byte)remaining;
                        index += remaining;

                        //Send buffer
                        conn.SendMessage(buffer, System.Net.WebSockets.WebSocketMessageType.Binary);
                    }
                    canvas_data.Position = canvas_data.Length;
                    Console.WriteLine("unlocking");
                }
                Console.WriteLine("unlocked");
            } catch (Exception ex)
            {
                Console.WriteLine("ERROR SENDING DATA " + ex.Message + ex.StackTrace);
            }

            //Send ready signal
            conn.SendMessage(WebsocketMessageOpcode.SetStateFlag, new SetStateFlagMessage
            {
                state = 1,
                resume_token = conn.resume_token
            });
        }
    }
}
