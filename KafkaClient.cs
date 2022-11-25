﻿
namespace Opc.Ua.Cloud.Commander
{
    using Confluent.Kafka;
    using Newtonsoft.Json;
    using Serilog;
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class KafkaClient
    {
        private ApplicationConfiguration _appConfig = null;

        private IProducer<Null, string> _producer = null;
        private IConsumer<Ignore, string> _consumer = null;

        public KafkaClient(ApplicationConfiguration appConfig)
        {
            _appConfig = appConfig;
        }

        public void Connect()
        {
            try
            {
                // create Kafka client
                var config = new ProducerConfig {
                    BootstrapServers = Environment.GetEnvironmentVariable("BROKERNAME") + ":9093",
                    RequestTimeoutMs = 20000,
                    MessageTimeoutMs = 10000,
                    SecurityProtocol = SecurityProtocol.SaslSsl,
                    SaslMechanism = SaslMechanism.Plain,
                    SaslUsername = Environment.GetEnvironmentVariable("USERNAME"),
                    SaslPassword = Environment.GetEnvironmentVariable("PASSWORD")
                };

                // If serializers are not specified, default serializers from
                // `Confluent.Kafka.Serializers` will be automatically used where
                // available. Note: by default strings are encoded as UTF8.
                _producer = new ProducerBuilder<Null, string>(config).Build();

                var conf = new ConsumerConfig
                {
                    GroupId = "consumer-group",
                    BootstrapServers = Environment.GetEnvironmentVariable("BROKERNAME") + ":9093",
                    // Note: The AutoOffsetReset property determines the start offset in the event
                    // there are not yet any committed offsets for the consumer group for the
                    // topic/partitions of interest. By default, offsets are committed
                    // automatically, so in this example, consumption will only start from the
                    // earliest message in the topic 'my-topic' the first time you run the program.
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    SecurityProtocol= SecurityProtocol.SaslSsl,
                    SaslMechanism = SaslMechanism.Plain,
                    SaslUsername = Environment.GetEnvironmentVariable("USERNAME"),
                    SaslPassword= Environment.GetEnvironmentVariable("PASSWORD")
                };

                _consumer = new ConsumerBuilder<Ignore, string>(conf).Build();

                _consumer.Subscribe(Environment.GetEnvironmentVariable("TOPIC"));

                _ = Task.Run(() => HandleCommand());

                Log.Logger.Information("Connected to Kafka broker.");

            }
            catch (Exception ex)
            {
                Log.Logger.Error("Failed to connect to Kafka broker: " + ex.Message);
            }
        }

        public void Publish(string payload)
        {
            Message<Null, string> message = new()
            {
                Headers = new Headers() { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
                Value = payload
            };

            _producer.ProduceAsync(Environment.GetEnvironmentVariable("RESPONSE_TOPIC"), message).GetAwaiter().GetResult();
        }

        // handles all incoming commands form the cloud
        private void HandleCommand()
        {
            while (true)
            {
                Thread.Sleep(1000);

                try
                {
                    ConsumeResult<Ignore, string> result = _consumer.Consume();

                    Log.Logger.Information($"Received method call with topic: {result.Topic} and payload: {result.Message.Value}");

                    string requestTopic = Environment.GetEnvironmentVariable("TOPIC");
                    string requestID = result.Topic.Substring(result.Topic.IndexOf("?"));

                    string requestPayload = result.Message.Value;
                    string responsePayload = string.Empty;

                    // route this to the right handler
                    if (result.Topic.StartsWith(requestTopic.TrimEnd('#') + "Command"))
                    {
                        new UAClient().ExecuteUACommand(_appConfig, requestPayload);
                        responsePayload = "Success";
                    }
                    else if (result.Topic.StartsWith(requestTopic.TrimEnd('#') + "Read"))
                    {
                        responsePayload = new UAClient().ReadUAVariable(_appConfig, requestPayload);
                    }
                    else if (result.Topic.StartsWith(requestTopic.TrimEnd('#') + "Write"))
                    {
                        new UAClient().WriteUAVariable(_appConfig, requestPayload);
                        responsePayload = "Success";
                    }
                    else
                    {
                        Log.Logger.Error("Unknown command received: " + result.Topic);
                    }

                    // send reponse to Kafka broker
                    Publish(JsonConvert.SerializeObject(responsePayload));
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "HandleMessageAsync");

                    // send error to Kafka broker
                    Publish(JsonConvert.SerializeObject(ex.Message));
                }
            }
        }
    }
}