using System;
using System.IO;
using System.Net;
using System.Collections.Specialized;
using Common.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImportImage
{
    class Program
    {
        static string rootDirectory = System.Configuration.ConfigurationManager.AppSettings["rootDirectory"].ToLower();
        static string postUrl = System.Configuration.ConfigurationManager.AppSettings["postUrl"];
        static bool isScanSpecificDirectory = Convert.ToBoolean(System.Configuration.ConfigurationManager.AppSettings["isScanSpecificDirectory"]);

        static List<string> ignoreExtensionList = System.Configuration.ConfigurationManager.AppSettings["ignoreExtension"].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        static List<string> ignoreDirectoryList = System.Configuration.ConfigurationManager.AppSettings["ignoreDirectory"].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        static List<string> specificDirectoryList = System.Configuration.ConfigurationManager.AppSettings["specificDirectory"].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        static void Main(string[] args)
        {
            
            if (isScanSpecificDirectory)
            {
                //扫描指定目录
                foreach (var item in specificDirectoryList)
                {
                    string tempDirName = string.Format("{0}{1}", rootDirectory, item.ToLower());
                    ListFiles(new DirectoryInfo(tempDirName));
                }
            }
            else {
                ListFiles(new DirectoryInfo(rootDirectory));    
            }
        }
        static void ListFiles(FileSystemInfo info)
        {
            if (!info.Exists) return;

            DirectoryInfo dir = info as DirectoryInfo;
            //不是目录
            if (dir == null) return;

            FileSystemInfo[] files = dir.GetFileSystemInfos();
            for (int i = 0; i < files.Length; i++)
            {
                FileInfo file = files[i] as FileInfo;
                //是文件
                if (file != null)
                {
                    
                    #region == POST ==
                    try
                    {
                        string fullName = file.FullName;

                        //目录或者文件是否含有中文，如果有，跳过
                        bool isChinese = Regex.IsMatch(fullName, @"[\u4e00-\u9fa5]+");

                        //判断目录是否合法
                        string dirName = file.Directory.Name;
                        string extension = Path.GetExtension(fullName);
                        if (!isChinese &&
                            !string.IsNullOrEmpty(extension) &&
                            !ignoreExtensionList.Exists(p => p == extension)
                            )
                        {
                            //替换掉路径，并把[\]替换为[/]
                            string aliasName = fullName.Replace(rootDirectory, string.Empty).Replace("\\", "/").ToLower();

                            Console.WriteLine(aliasName);

                            byte[] bytes = null;
                            //获得文件流
                            using (FileStream fileStream = new FileStream(fullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                // 读取文件的 byte[] 
                                bytes = new byte[fileStream.Length];
                                fileStream.Read(bytes, 0, bytes.Length);
                            }
                            if (bytes != null)
                            {
                                //上传文件
                                UploadFile(aliasName, bytes);

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        //记录日志
                        LogManager.GetLogger(typeof(Program)).Error("出错", ex);
                    }
                    #endregion
                     
                }
                //对于子目录，进行递归调用
                else
                {

                    var childDir = files[i];
                    string dirName = childDir.FullName.Replace(rootDirectory, string.Empty).Replace("\\", "/").ToLower();

                    if (!ignoreDirectoryList.Exists(item => item.ToLower() == dirName))
                    {
                        ListFiles(childDir);
                    }
                }

            }
        }
        static void UploadFile(string aliasName, byte[] buffer)
        {

            //执行三次，如果三次后，有错误，则记日志
            int max = 3;
            while (max-- > 0)
            {
                //POST到服务器上
                string result = UploadFileToFastDFS(aliasName, buffer, new NameValueCollection() { });
                if (result.ToLower() == "true")
                {
                    //跳出
                    break;
                }
                if (max == 0)
                {
                    //记日志
                    string remarks = string.Format("alias_name:{0},{1}", aliasName, result);
                    LogManager.GetLogger(typeof(Program)).Error(new log4net.Extension.LogMessage("导图至FastDFS失败",
                        "127.0.0.1", "127.0.0.1", remarks), null);
                }
            }
        }
        static string UploadFileToFastDFS(string aliasName, byte[] buffer, NameValueCollection nvc)
        {
            string result = string.Empty;

            HttpWebRequest webRequest = null;
            WebResponse webResponse = null;
            StreamReader responseReader = null;
            Stream requestStream = null;
            Stream responseStream = null;
            try
            {
                string boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
                byte[] boundarybytes = System.Text.Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");

                webRequest = (HttpWebRequest)WebRequest.Create(postUrl);
                webRequest.ContentType = "multipart/form-data; boundary=" + boundary;
                webRequest.Method = "POST";
                webRequest.KeepAlive = false;

                double length = buffer.LongLength;
                //如果文件小于4M，超时时间3秒，否则使用默认超时时间
                //理论上传不了这么大的图
                if (buffer.Length < 4 * 1024 * 1024)
                {
                    webRequest.Timeout = 1000 * 3;
                }

                requestStream = webRequest.GetRequestStream();



                string formdataTemplate = "Content-Disposition: form-data; name=\"{0}\"\r\n\r\n{1}";

                requestStream.Write(boundarybytes, 0, boundarybytes.Length);
                string formitem = string.Format(formdataTemplate, "alias_name", aliasName);
                byte[] formitembytes = System.Text.Encoding.UTF8.GetBytes(formitem);
                requestStream.Write(formitembytes, 0, formitembytes.Length);


                foreach (string key in nvc.Keys)
                {
                    requestStream.Write(boundarybytes, 0, boundarybytes.Length);
                    formitem = string.Format(formdataTemplate, key, nvc[key]);
                    formitembytes = System.Text.Encoding.UTF8.GetBytes(formitem);
                    requestStream.Write(formitembytes, 0, formitembytes.Length);
                }
                requestStream.Write(boundarybytes, 0, boundarybytes.Length);

                string headerTemplate = "Content-Disposition: form-data; name=\"upfile\"; filename=\"{0}\"\r\nContent-Type: {1}\r\n\r\n";
                string header = string.Format(headerTemplate, aliasName, string.Empty);
                byte[] headerbytes = System.Text.Encoding.UTF8.GetBytes(header);
                requestStream.Write(headerbytes, 0, headerbytes.Length);

                requestStream.Write(buffer, 0, buffer.Length);

                byte[] trailer = System.Text.Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
                requestStream.Write(trailer, 0, trailer.Length);
                requestStream.Close();




                webResponse = webRequest.GetResponse();
                responseStream = webResponse.GetResponseStream();
                responseReader = new StreamReader(responseStream);

                result = responseReader.ReadToEnd();
            }
            catch (Exception ex)
            {
                //记日志
                LogManager.GetLogger(typeof(Program)).Error(new log4net.Extension.LogMessage("导图Request失败",
                        "127.0.0.1", "127.0.0.1", string.Empty), ex);
            }
            finally
            {
                if (webResponse != null)
                {
                    webResponse.Close();
                    webResponse = null;
                }
                if (responseReader != null)
                {
                    responseReader.Close();
                    responseReader = null;
                }
                if (responseStream != null)
                {
                    responseStream.Close();
                    responseStream = null;
                }
                requestStream = null;
                webRequest = null;
            }
            return result;
        }
    }
}
