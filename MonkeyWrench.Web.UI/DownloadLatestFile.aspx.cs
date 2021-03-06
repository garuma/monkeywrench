﻿/*
 * DownloadLatestFile.aspx.cs
 *
 * Authors:
 *   Rolf Bjarne Kvinge (RKvinge@novell.com)
 *   
 * Copyright 2009 Novell, Inc. (http://www.novell.com)
 *
 * See the LICENSE file included with the distribution for details.
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Web;
using System.Net;

using MonkeyWrench;
using MonkeyWrench.Web.WebServices;

public partial class DownloadLatestFile : System.Web.UI.Page
{
	protected void Page_Load (object sender, EventArgs e)
	{
		int? lane_id = null;
		string lane = null;
		string filename = null;
		bool completed = false;
		bool successful = false;
		int tmp;

		lane = Request ["lane"];
		if (int.TryParse (Request ["lane_id"], out tmp))
			lane_id = tmp;

		bool.TryParse (Request ["completed"], out completed);
		bool.TryParse (Request ["successful"], out successful);

		filename = Request ["filename"];

		using (WebServices ws = WebServices.Create ()) {
			int? id = ws.FindLatestWorkFileId (ws.WebServiceLogin, lane_id, lane, filename, completed, successful);

			if (id == null)
				throw new HttpException (404, "File not found");

			Response.Redirect ("GetFile.aspx?id=" + id.Value.ToString (), false);
		}
	}
}