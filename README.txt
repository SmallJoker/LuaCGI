 Lua for FastCGI
=================

Copyright (C) 2015 Krock/SmallJoker <mk939@ymail.com>

This C# project provides you an (experimental) application to embed Lua in websites.
Successfully tested with Nginx 1.9.5 on Windows.

Included Lua library file version: LuaJIT 5.1.4 (developement version)
License: LGPL 2.1 (see LICENSE.txt and/or tldrlegal.com)

  Setup
---------

To get this stuff working with Nginx, apply following lines:

File: conf\nginx.conf
>	server {
>		...
>
>		location / {
>			...
>			index		index.php index.html index.htm;
>		}
>		location ~ .lua$ {
>			fastcgi_pass	127.0.0.1:9000;
>			fastcgi_index	index.lua;
>			fastcgi_param	SCRIPT_FILENAME $document_root$fastcgi_script_name;
>			include		fastcgi_params;
>		}
>	}

File: conf\mime.types (optional .. ??)
>	text/html		html htm shtml php lua;

File: <html root>\test.lua
>	<h1>Testing LuaCGI</h1>
>	Total Lua run time: <?lua return os.clock() .. " seconds" ?><br />
>	Your useragent is <?lua return HEAD.HTTP_USER_AGENT


 TODO
------

- Add some embedding examples
