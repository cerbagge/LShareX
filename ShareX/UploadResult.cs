#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;

namespace ShareX
{
    // LShareX: ShareX.UploadersLib was removed. Tasks still keep their result here: the URL fields of older
    // history entries and the error messages of a task. Nothing is uploaded anymore.
    public class UploadResult
    {
        public string URL { get; set; }
        public string ThumbnailURL { get; set; }
        public string DeletionURL { get; set; }
        public string ShortenedURL { get; set; }
        public string Response { get; set; }
        public TaskErrors Errors { get; } = new TaskErrors();
        public bool IsURLExpected { get; set; }

        public bool IsError => Errors.Count > 0 && (!IsURLExpected || string.IsNullOrEmpty(URL));

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(ShortenedURL))
            {
                return ShortenedURL;
            }

            return URL ?? "";
        }

        public string ErrorsToString()
        {
            return IsError ? Errors.ToString() : null;
        }
    }

    public class TaskErrors : List<string>
    {
        public override string ToString()
        {
            return string.Join(Environment.NewLine + Environment.NewLine, this);
        }
    }
}
