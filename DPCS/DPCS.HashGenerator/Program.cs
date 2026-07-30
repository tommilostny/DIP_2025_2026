using DPCS.HashGenerator.Services;

var options = await CliService.ParseAsync(args);
var generator = new SampleGenerationService(options);
generator.GenerateAll();
