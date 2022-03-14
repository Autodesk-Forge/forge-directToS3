using Autodesk.Forge;
using Autodesk.Forge.Model;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;

namespace Forge_Upload_DirectToS3
{
  class Program
  {
    public static string BASE_URL = "https://developer.api.autodesk.com/oss/v2";

    public static dynamic Credential = null;

    public static int UPLOAD_CHUNK_SIZE = 5 * 1024 * 1024;

    public static string clientId = "";
    public static string clientSecret = "";

    public static int MaxRetry = 5;

    static async Task Main(string[] args)
    {
      try
      {
        var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

        clientId = configuration.GetSection("Forge_Client_Id").Value;
        clientSecret = configuration.GetSection("Forge_Client_Secret").Value;
        string bucketKey = configuration.GetSection("BucketKey").Value;
        string projectId = configuration.GetSection("ProjectId").Value;
        string folderId = configuration.GetSection("Folder_Urn").Value;

        Credential = await Get2LeggedTokenAsync(new Scope[] { Scope.BucketCreate, Scope.BucketRead, Scope.BucketDelete, Scope.DataRead, Scope.DataWrite, Scope.DataCreate, Scope.CodeAll });

        FileStream uploadFileStream = new FileStream(args[0], FileMode.Open);

        string[] fileAux = uploadFileStream.Name.Split('/');
        string filename = fileAux[fileAux.Length - 1];

        switch (args[1].ToLower())
        {
          case "docs":
            // This one for upload to a specific Docs Folder
            await UploadToDocs(uploadFileStream, filename, projectId, folderId);
            Console.WriteLine("Upload Complete!");
            break;
          case "bucket":
            // This one for uploading to Forge app managed buckets
            await UploadToBucket(bucketKey, filename, uploadFileStream);
            Console.WriteLine("Upload Complete!");
            break;
          default:
            Console.WriteLine("Please specify docs or buckets as second argument for upload method");
            break;
        }
      }
      catch (Exception ex)
      {
        System.Console.WriteLine(ex.Message);
        System.Console.WriteLine(ex.StackTrace);
      }
    }

    //Here we retrieve the urls for uploading the chunks of our file directly to the specified bucket
    private static async Task<dynamic> getUploadUrls(string bucketKey, string objectKey, int parts = 1, int firstPart = 1, string uploadKey = null)
    {
      string endpoint = $"/buckets/{bucketKey}/objects/{HttpUtility.UrlEncode(objectKey)}/signeds3upload";

      RestClient client = new RestClient(BASE_URL);
      RestRequest request = new RestRequest(endpoint, RestSharp.Method.GET);
      request.AddHeader("Authorization", "Bearer " + Credential.access_token);
      request.AddParameter("parts", parts, ParameterType.QueryString);
      request.AddParameter("firstPart", firstPart, ParameterType.QueryString);

      if (!string.IsNullOrEmpty(uploadKey))
      {
        request.AddParameter("uploadKey", uploadKey, ParameterType.QueryString);
      }

      var response = await client.ExecuteAsync(request);

      //Here we handle 429 for Get Upload URLs
      if (response.StatusCode == HttpStatusCode.TooManyRequests)
      {
        int retryAfter = 0;
        int.TryParse(response.Headers.ToList()
            .Find(x => x.Name == "Retry-After")
            .Value.ToString(), out retryAfter);
        Task.WaitAll(Task.Delay(retryAfter));
        return await getUploadUrls(bucketKey, objectKey, parts, firstPart, uploadKey);
      }

      return JsonConvert.DeserializeObject(response.Content);
    }

    private static async Task<dynamic> UploadToBucket(string bucketKey, string objectKey, FileStream fileStream)
    {
      long fileSize = fileStream.Length;
      int maxBatches = 25;
      int numberOfChunks = (int)Math.Round((double)(fileSize / UPLOAD_CHUNK_SIZE)) + 1;
      int partsUploaded = 0;
      long start = 0;
      List<string> uploadUrls = new List<string>();
      string uploadKey = null;

      using (BinaryReader reader = new BinaryReader(fileStream))
      {
        while (partsUploaded < numberOfChunks)
        {
          int attempts = 0;

          long end = Math.Min((partsUploaded + 1) * UPLOAD_CHUNK_SIZE, fileSize);

          long numberOfBytes = end - start;
          byte[] fileBytes = new byte[numberOfBytes];
          reader.BaseStream.Seek((int)start, SeekOrigin.Begin);
          int count = reader.Read(fileBytes, 0, (int)numberOfBytes);

          while (true)
          {
            attempts++;
            Console.WriteLine($"Uploading part {partsUploaded + 1}, attempt {attempts}");
            if (uploadUrls.Count == 0)
            {
              Credential = await Get2LeggedTokenAsync(new Scope[] { Scope.BucketCreate, Scope.BucketRead, Scope.BucketDelete, Scope.DataRead, Scope.DataWrite, Scope.DataCreate, Scope.CodeAll });
              dynamic uploadParams = await getUploadUrls(bucketKey, objectKey, Math.Min(numberOfChunks - partsUploaded, maxBatches), partsUploaded + 1, uploadKey);
              uploadKey = uploadParams.uploadKey;
              uploadUrls = uploadParams.urls.ToObject<List<string>>();
            }

            string currentUrl = uploadUrls[0];
            uploadUrls.RemoveAt(0);

            try
            {
              var responseBuffer = await UploadBufferRestSharp(currentUrl, fileBytes);

              int statusCode = (int)responseBuffer.StatusCode;

              switch (statusCode)
              {
                case 403:
                  Console.WriteLine("403, refreshing urls");
                  uploadUrls = new List<string>();
                  break;
                case int n when (n >= 400):
                  throw new Exception(responseBuffer.Content);
                default:
                  goto NextChunk;
              }

            }
            catch (Exception ex)
            {
              Console.WriteLine(ex.Message);
              if (attempts == MaxRetry)
                throw;
            }
          }
        NextChunk:
          partsUploaded++;
          start = end;
          System.Console.WriteLine($"{partsUploaded.ToString()} parts uploaded!");

        }
      }

      var responseUpload = await CompleteUpload(bucketKey, objectKey, uploadKey);

      return responseUpload;
    }

    private static async Task<dynamic> UploadBufferRestSharp(string url, byte[] buffer)
    {
      RestClient client = new RestClient();
      RestRequest request = new RestRequest(url, RestSharp.Method.PUT);
      request.AddParameter("", buffer, ParameterType.RequestBody);

      var response = await client.ExecuteAsync(request);

      return response;
    }

    //This function is very similar to the one at  https://github.com/Autodesk-Forge/forge-viewhubs/blob/master/forgeSample/Controllers/DataManagementController.cs#L315-L452
    public static async Task<dynamic> UploadToDocs(FileStream fileStream, string fileName, string projectId, string folderId)
    {

      // prepare storage
      ProjectsApi projectApi = new ProjectsApi();
      projectApi.Configuration.AccessToken = Credential.access_token;
      StorageRelationshipsTargetData storageRelData = new StorageRelationshipsTargetData(StorageRelationshipsTargetData.TypeEnum.Folders, folderId);
      CreateStorageDataRelationshipsTarget storageTarget = new CreateStorageDataRelationshipsTarget(storageRelData);
      CreateStorageDataRelationships storageRel = new CreateStorageDataRelationships(storageTarget);
      BaseAttributesExtensionObject attributes = new BaseAttributesExtensionObject(string.Empty, string.Empty, new JsonApiLink(string.Empty), null);
      CreateStorageDataAttributes storageAtt = new CreateStorageDataAttributes(fileName, attributes);
      CreateStorageData storageData = new CreateStorageData(CreateStorageData.TypeEnum.Objects, storageAtt, storageRel);
      CreateStorage storage = new CreateStorage(new JsonApiVersionJsonapi(JsonApiVersionJsonapi.VersionEnum._0), storageData);
      dynamic storageCreated = await projectApi.PostStorageAsync(projectId, storage);

      string[] storageIdParams = ((string)storageCreated.data.id).Split('/');
      string[] bucketKeyParams = storageIdParams[storageIdParams.Length - 2].Split(':');
      string bucketKey = bucketKeyParams[bucketKeyParams.Length - 1];
      string objectName = storageIdParams[storageIdParams.Length - 1];

      // upload the file/object, which will create a new object
      ObjectsApi objects = new ObjectsApi();
      objects.Configuration.AccessToken = Credential.access_token;

      //This is the only difference from the old method
      var response = await UploadToBucket(bucketKey, objectName, fileStream);

      if ((int)response.StatusCode >= 400)
      {
        throw new Exception(response.Content);
      }

      // check if file already exists...
      FoldersApi folderApi = new FoldersApi();
      folderApi.Configuration.AccessToken = Credential.access_token;
      var filesInFolder = await folderApi.GetFolderContentsAsync(projectId, folderId);
      string itemId = string.Empty;
      foreach (KeyValuePair<string, dynamic> item in new DynamicDictionaryItems(filesInFolder.data))
        if (item.Value.attributes.displayName == fileName)
          itemId = item.Value.id; // this means a file with same name is already there, so we'll create a new version

      // now decide whether create a new item or new version
      if (string.IsNullOrWhiteSpace(itemId))
      {
        // create a new item
        BaseAttributesExtensionObject baseAttribute = new BaseAttributesExtensionObject(projectId.StartsWith("a.") ? "items:autodesk.core:File" : "items:autodesk.bim360:File", "1.0");
        CreateItemDataAttributes createItemAttributes = new CreateItemDataAttributes(fileName, baseAttribute);
        CreateItemDataRelationshipsTipData createItemRelationshipsTipData = new CreateItemDataRelationshipsTipData(CreateItemDataRelationshipsTipData.TypeEnum.Versions, CreateItemDataRelationshipsTipData.IdEnum._1);
        CreateItemDataRelationshipsTip createItemRelationshipsTip = new CreateItemDataRelationshipsTip(createItemRelationshipsTipData);
        StorageRelationshipsTargetData storageTargetData = new StorageRelationshipsTargetData(StorageRelationshipsTargetData.TypeEnum.Folders, folderId);
        CreateStorageDataRelationshipsTarget createStorageRelationshipTarget = new CreateStorageDataRelationshipsTarget(storageTargetData);
        CreateItemDataRelationships createItemDataRelationhips = new CreateItemDataRelationships(createItemRelationshipsTip, createStorageRelationshipTarget);
        CreateItemData createItemData = new CreateItemData(CreateItemData.TypeEnum.Items, createItemAttributes, createItemDataRelationhips);
        BaseAttributesExtensionObject baseAttExtensionObj = new BaseAttributesExtensionObject(projectId.StartsWith("a.") ? "versions:autodesk.core:File" : "versions:autodesk.bim360:File", "1.0");
        CreateStorageDataAttributes storageDataAtt = new CreateStorageDataAttributes(fileName, baseAttExtensionObj);
        CreateItemRelationshipsStorageData createItemRelationshipsStorageData = new CreateItemRelationshipsStorageData(CreateItemRelationshipsStorageData.TypeEnum.Objects, storageCreated.data.id);
        CreateItemRelationshipsStorage createItemRelationshipsStorage = new CreateItemRelationshipsStorage(createItemRelationshipsStorageData);
        CreateItemRelationships createItemRelationship = new CreateItemRelationships(createItemRelationshipsStorage);
        CreateItemIncluded includedVersion = new CreateItemIncluded(CreateItemIncluded.TypeEnum.Versions, CreateItemIncluded.IdEnum._1, storageDataAtt, createItemRelationship);
        CreateItem createItem = new CreateItem(new JsonApiVersionJsonapi(JsonApiVersionJsonapi.VersionEnum._0), createItemData, new List<CreateItemIncluded>() { includedVersion });

        ItemsApi itemsApi = new ItemsApi();
        itemsApi.Configuration.AccessToken = Credential.access_token;
        var newItem = await itemsApi.PostItemAsync(projectId, createItem);
        return newItem;
      }
      else
      {
        // create a new version
        BaseAttributesExtensionObject attExtensionObj = new BaseAttributesExtensionObject(projectId.StartsWith("a.") ? "versions:autodesk.core:File" : "versions:autodesk.bim360:File", "1.0");
        CreateStorageDataAttributes storageDataAtt = new CreateStorageDataAttributes(fileName, attExtensionObj);
        CreateVersionDataRelationshipsItemData dataRelationshipsItemData = new CreateVersionDataRelationshipsItemData(CreateVersionDataRelationshipsItemData.TypeEnum.Items, itemId);
        CreateVersionDataRelationshipsItem dataRelationshipsItem = new CreateVersionDataRelationshipsItem(dataRelationshipsItemData);
        CreateItemRelationshipsStorageData itemRelationshipsStorageData = new CreateItemRelationshipsStorageData(CreateItemRelationshipsStorageData.TypeEnum.Objects, storageCreated.data.id);
        CreateItemRelationshipsStorage itemRelationshipsStorage = new CreateItemRelationshipsStorage(itemRelationshipsStorageData);
        CreateVersionDataRelationships dataRelationships = new CreateVersionDataRelationships(dataRelationshipsItem, itemRelationshipsStorage);
        CreateVersionData versionData = new CreateVersionData(CreateVersionData.TypeEnum.Versions, storageDataAtt, dataRelationships);
        CreateVersion newVersionData = new CreateVersion(new JsonApiVersionJsonapi(JsonApiVersionJsonapi.VersionEnum._0), versionData);

        VersionsApi versionsApis = new VersionsApi();
        versionsApis.Configuration.AccessToken = Credential.access_token;
        dynamic newVersion = await versionsApis.PostVersionAsync(projectId, newVersionData);
        return newVersion;
      }
    }


    private static async Task<dynamic> CompleteUpload(string bucketKey, string objectKey, string uploadKey)
    {
      string endpoint = $"/buckets/{bucketKey}/objects/{HttpUtility.UrlEncode(objectKey)}/signeds3upload";
      RestClient client = new RestClient(BASE_URL);
      RestRequest request = new RestRequest(endpoint, RestSharp.Method.POST);

      request.AddHeader("Authorization", "Bearer " + Credential.access_token);
      request.AddHeader("Content-Type", "application/json");

      request.AddJsonBody(new { uploadKey = $"{uploadKey}" });

      var response = await client.ExecuteAsync(request);

      return response;
    }

    /// <summary>
    /// Get the access token from Autodesk
    /// </summary>
    private static async Task<dynamic> Get2LeggedTokenAsync(Scope[] scopes)
    {
      TwoLeggedApi oauth = new TwoLeggedApi();
      string grantType = "client_credentials";
      dynamic bearer = await oauth.AuthenticateAsync(
        clientId,
        clientSecret,
        grantType,
        scopes);
      return bearer;
    }
  }
}
