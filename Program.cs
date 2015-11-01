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
using System.Collections.Generic;
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

		Encoding enc = Encoding.UTF8;
		bool packet_lock;

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
				byte[] buf = new byte[cli.ReceiveBufferSize];
				cli.Receive(buf, 0, buf.Length, SocketFlags.None);

				// Read CGI protocol information (partwise)
				byte CGI_VER = buf[0],
					CGI_TYP = buf[1];
				ushort CGI_RID = (ushort)(buf[4] << 8 | buf[3]);

				Dictionary<string, string> headers = new Dictionary<string, string>(),
					head_GET = new Dictionary<string, string>(),
					head_POST = new Dictionary<string, string>();

				#region Read header fields
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
				#endregion

				#region Get GET data
				if (headers["QUERY_STRING"].Length > 0)
					ParseGET(headers["QUERY_STRING"], ref head_GET);
				#endregion

				int old_offset = offset;
				#region Get POST data
				if (headers.ContainsKey("CONTENT_TYPE")) {
					offset += 15;
					int data_length = buf[offset] << 8 | buf[offset + 1];
					offset += 4;
					string data = ReadString(ref buf, ref offset, data_length);

					string content_type = headers["CONTENT_TYPE"];
					if (content_type.StartsWith("multipart/form-data")) {
						// Encoded binary data
						int boundary_start = content_type.IndexOf("boundary=", StringComparison.Ordinal);
						string boundary = content_type.Remove(0, boundary_start + 9);

						// TODO: Parse that data, splitting by '\n'
					} else {
						// GET-Like data
						ParseGET(data, ref head_POST);
					}
				}
				#endregion

				buf = null;
				#endregion

				string path = headers["SCRIPT_FILENAME"].Replace('/', '\\');
				Console.WriteLine("Loading file: " + path);

				L.ResetLua();
				L.RegisterLuaFunction(l_print, "print");

				#region Global Lua tables
				// HTTP Headers
				L.CreateLuaTable("HEAD");
				Lua.lua_getglobal(L.L, "HEAD");
				foreach (KeyValuePair<string, string> e in headers)
					L.SetTableField(e.Key, e.Value);
				Lua.lua_pop(L.L, 1);

				// GET data
				L.CreateLuaTable("GET");
				Lua.lua_getglobal(L.L, "GET");
				foreach (KeyValuePair<string, string> e in head_GET)
					L.SetTableField(e.Key, e.Value);
				Lua.lua_pop(L.L, 1);

				// POST data
				L.CreateLuaTable("POST");
				Lua.lua_getglobal(L.L, "POST");
				foreach (KeyValuePair<string, string> e in head_POST)
					L.SetTableField(e.Key, e.Value);
				Lua.lua_pop(L.L, 1);
				#endregion

				packet = new StringBuilder(1024);
				packet.Append("Connection: close\n");
				packet.Append("Content-Type:	 text/html; charset=UTF-8\n\n");

				packet_lock = false;
				ParseHTML(path);
				L.CloseLua();

				byte[] content_l = enc.GetBytes(packet.ToString());
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
			while (packet_lock)
				System.Threading.Thread.Sleep(5);
			packet_lock = true;

			string text = Lua.lua_tostring(ptr, -1);
			packet.AppendLine(text);

			packet_lock = false;
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
			return enc.GetString(data);
		}

		void ParseHTML(string path)
		{
			if (!System.IO.File.Exists(path)) {
				packet.Append("<h1>404 - Not found</h1>The file was not found on this server.");
				return;
			}

			char[] content = enc.GetChars(System.IO.File.ReadAllBytes(path));

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

				while (packet_lock)
					System.Threading.Thread.Sleep(5);
				packet_lock = true;

				if (err > 0) {
					packet.Append("<pre>Lua error near line: " + lua_line + "\n");
					packet.Append(System.Security.SecurityElement.Escape(output));
					packet.Append("</pre>");
				} else if (output != null) {
					packet.Append(output);
				}

				packet_lock = false;
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

		void ParseGET(string query, ref Dictionary<string, string> dst)
		{
			string[] args = query.Split('&');
			foreach (string arg in args) {
				string[] key_value = arg.Split('=');

				if (key_value.Length == 2) {
					dst[key_value[0]] = key_value[1];
					continue;
				}
				dst[key_value[0]] = "";
			}
		}

		//void ParsePOST(string data, ref int offset)
	}
}