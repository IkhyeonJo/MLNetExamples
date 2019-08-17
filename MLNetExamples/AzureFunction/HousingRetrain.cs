using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Trainers;
using Microsoft.WindowsAzure.Storage;

namespace AzureFunction
{
    public class HousingRetrain
    {
        [FunctionName("HousingRetrain")]
        public async Task Run([BlobTrigger("input/{name}", Connection = "AzureWebJobsStorage")]Stream myBlob, string name, ILogger log)
        {
            log.LogInformation($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Length} Bytes");
            string blobData;

            // Download model files from Blob Storage, if needed
            string trainerFilePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "models/housing-trainer.zip");
            string pipelineFilePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "models/housing-data-prep.zip");

            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);

            var storageAccount = CloudStorageAccount.Parse(connectionString);

            var client = storageAccount.CreateCloudBlobClient();

            var container = client.GetContainerReference("models");

            var dataModel = container.GetBlockBlobReference("housing-data-prep.zip");
            var trainingModel = container.GetBlockBlobReference("housing-trainer.zip");

            if (!File.Exists(pipelineFilePath))
            {
                await dataModel.DownloadToFileAsync(pipelineFilePath, FileMode.Create);
            }

            if (!File.Exists(trainerFilePath))
            {
                await trainingModel.DownloadToFileAsync(trainerFilePath, FileMode.Create);
            }

            // Load models into ML Context
            var context = new MLContext();

            DataViewSchema modelSchema, pipelineSchema;

            var trainerModel = context.Model.Load(trainerFilePath, out modelSchema);
            var dataPrepModel = context.Model.Load(pipelineFilePath, out pipelineSchema);

            var originalModelParams =
                ((ISingleFeaturePredictionTransformer<object>)trainerModel).Model as LinearModelParameters;

            // Read and parse blob data
            using (var reader = new StreamReader(myBlob))
            {
                blobData = reader.ReadToEnd();
            }

            var parsedData = blobData
                .Split('\n')
                .Skip(1)
                .Select(line => line.Split(','))
                .TakeWhile(row => !string.IsNullOrWhiteSpace(row[0]))
                .Select(row => new HousingData
                {
                    Longitude = float.Parse(row[0]),
                    Latitude = float.Parse(row[1]),
                    HousingMedianAge = float.Parse(row[2]),
                    TotalRooms = float.Parse(row[3]),
                    TotalBedrooms = float.Parse(row[4]),
                    Population = float.Parse(row[4]),
                    Households = float.Parse(row[5]),
                    MedianIncome = float.Parse(row[6]),
                    MedianHouseValue = float.Parse(row[7]),
                    OceanProximity = row[8]
                });

            // Load new data and build new model based off original parameters
            var newData = context.Data.LoadFromEnumerable(parsedData);

            var newDataTransformed = dataPrepModel.Transform(newData);

            var retrainedModel = context.Regression.Trainers.LbfgsPoissonRegression()
                .Fit(newDataTransformed, originalModelParams);

            // Compare model params
            var newModelParams = retrainedModel.Model as PoissonRegressionModelParameters;

            var weightDiffs = originalModelParams.Weights.Zip(
                newModelParams.Weights, (original, updated) => original - updated).ToArray();

            Console.WriteLine("Original\tRetrained\tDifference");
            for (int i = 0; i < weightDiffs.Count(); i++)
            {
                Console.WriteLine($"{originalModelParams.Weights[i]}\t{newModelParams.Weights[i]}\t{weightDiffs[i]}");
            }
        }
    }
}
