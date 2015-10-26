/*
Lua CGI application for Nginx
Copyright (C) 2015 Krock/SmallJoker <mk939@ymail.com>


This library is free software; you can redistribute it and/or
modify it under the terms of the GNU Lesser General Public
License as published by the Free Software Foundation; either
version 2.1 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Lesser General Public License for more details.

You should have received a copy of the GNU Lesser General Public
License along with this library; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
*/

using System;
using System.Text;
using System.Net.Sockets;
using Tuxen.Utilities.Lua;

namespace LuaCGI
{
	class Program
	{
		static void Main(string[] args)
		{
			new Engine();
		}
	}

	class Engine
	{
		static System.Net.IPEndPoint END_POINT = new System.Net.IPEndPoint(
					new System.Net.IPAddress(new byte[] { 127, 0, 0, 1 }),
					9000);

		ScriptEngine L;
		TcpListener host;
		StringBuilder packet;

		Encoding enc_head = Encoding.ASCII,
			enc_file = Encoding.UTF8;

		public Engine()
		{
			L = new ScriptEngine();
			host = new TcpListener(END_POINT);
			host.Start();
			Console.WriteLine("Started TCP listender on port " + END_POINT.Address);
			Listen();
		}

		void Listen()
		{
			System.Collections.Generic.Dictionary<string, string> headers;
			while (true) {
				#region Accept stream
				Socket cli = host.AcceptSocket();

				if (cli.SocketType != SocketType.Stream) {
					Console.WriteLine("Rejected socket type: " + cli.SocketType);
					cli.Close();
					continue;
				}
				string remote_address = ((System.Net.IPEndPoint)cli.RemoteEndPoint).Address.ToString();
				if (remote_address != END_POINT.Address.ToString()) {
					Console.WriteLine("Rejected external request: " + cli.RemoteEndPoint);
					cli.Close();
					continue;
				}

				if (!cli.Connected)
					continue;

				DateTime clock0 = DateTime.Now,
					clock1;

				#endregion
				#region Parse FastCGI
				byte[] buf = new byte[Math.Min(1024, cli.ReceiveBufferSize)];
				cli.Receive(buf, 0, buf.Length, SocketFlags.None);

				// Read CGI protocol information (partwise)
				byte CGI_VER = buf[0],
					CGI_TYP = buf[1];
				ushort CGI_RID = (ushort)(buf[4] << 8 | buf[3]);

				headers = new System.Collections.Generic.Dictionary<string, string>();

				// Magical index where header fields start being sent
				int offset = 24;
				while (offset < buf.Length) {
					byte key_l = buf[offset],
						value_l = buf[offset + 1];
					offset += 2;

					string key = ReadString(ref buf, ref offset, key_l),
						value = ReadString(ref buf, ref offset, value_l);

					if (key_l == 0)
						break;

					headers[key] = value;
				}
				buf = null;
				#endregion

				string path = headers["DOCUMENT_ROOT"] + headers["SCRIPT_NAME"];
				path = Uri.UnescapeDataString(path.Replace('/', '\\'));
				Console.WriteLine("Loading file: " + path);

				L.ResetLua();
				L.RegisterLuaFunction(l_print, "print");
				#region Add all HTTP headers to table HEAD
				L.CreateLuaTable("HEAD");
				Lua.lua_getglobal(L.L, "HEAD");
				foreach (System.Collections.Generic.KeyValuePair<string, string> e in headers)
					L.SetTableField(e.Key, e.Value);
				Lua.lua_pop(L.L, 1);
				#endregion

				packet = new StringBuilder(1024);
				packet.Append("Connection: close\n");
				packet.Append("Content-Type:	 text/html; charset=UTF-8\n\n");

				ParseHTML(path);
				L.CloseLua();

				byte[] content_l = enc_file.GetBytes(packet.ToString());
				cli.Send(MakeHead(CGI_RID, content_l.Length));
				cli.Send(content_l);

				//L.LogError();
				//foreach (string s in L.Errors)
				//	Console.WriteLine(s);
				//System.Threading.Thread.Sleep(100);

				cli.Close();
				clock1 = DateTime.Now;
				TimeSpan diff = clock1 - clock0;
				Console.WriteLine("\tExecution took " + diff.Milliseconds + " ms");
			}
		}

		int l_print(IntPtr ptr)
		{
			string text = Lua.lua_tostring(ptr, -1);
			packet.AppendLine(text);
			return 0;
		}

		byte[] MakeHead(ushort requestId, int content_l)
		{
			return new byte[] {
				1,							/* Version */
				6,							/* Send type STDOUT */
				(byte)(requestId >> 8),
				(byte)requestId,
				(byte)(content_l >> 8),
				(byte)content_l,
				0,
				0
			};
		}

		string ReadString(ref byte[] buf, ref int offset, int count)
		{
			byte[] data = new byte[count];
			System.Buffer.BlockCopy(buf, offset, data, 0, count);
			offset += count;
			return enc_head.GetString(data);
		}

		void ParseHTML(string path)
		{
			char[] content = enc_file.GetChars(System.IO.File.ReadAllBytes(path));

			int pos = 0,			/* File reading index */
				lua_start = -1,	/* Start index of Lua block */
				lua_end = 0,		/* Current Lua block ending index */
				last_end = 0,	/* Previous Lua block ending index */
				lines = 0,		/* Total lines of the file */
				lua_line = 0;   /* Line of starting Lua block */

			while (pos < content.Length) {
				lua_start = -1;
				lua_end = content.Length;

				#region Get start and end positions
				for (; pos < content.Length; ++pos) {
					char cur = content[pos];
					if (cur == '\n')
						lines++;

					if (cur != '?')
						continue;

					// '<?lua' opening tag
					if (pos < content.Length - 4 && lua_start < 0)
						if (new string(content, pos - 1, 5) == "<?lua") {
							lua_start = pos + 5;
							lua_line = lines;
						}

					// '?>' closing tag
					if (content[pos + 1] == '>') {
						lua_end = pos - 1;
						pos += 2;
						break;
					}
				}
				if (lua_start < 0)
					break;
				#endregion

				// Not sent HTML above Lua block
				if (last_end == 0) {
					packet.Append(content, 0, lua_start - 6);
				} else {
					int leftover = lua_start - last_end - 9;
					if (leftover > 0)
						packet.Append(content, last_end + 3, leftover);
				}

				// Ignore empty Lua blocks
				if (lua_end - lua_start < 5) {
					last_end = lua_end;
					continue;
				}

				#region Execute and log Lua block errors
				string run = new string(content, lua_start, lua_end - lua_start);
				int err = Lua.luaL_dostring(L.L, run);
				string output = Lua.lua_tostring(L.L, -1);

				if (err > 0) {
					packet.Append("<pre>Lua error near line: " + lua_line + "\n");
					packet.Append(System.Security.SecurityElement.Escape(output));
					packet.Append("</pre>");
				} else if (output != null) {
					packet.Append(output);
				}
				#endregion

				last_end = lua_end;
			}

			// End of file
			if (last_end == 0) {
				packet.Append(content, 0, content.Length);
			} else {
				int start = last_end + 3;
				int leftover = content.Length - start;
				if (leftover > 0)
					packet.Append(content, start, leftover);
			}
		}
	}
}