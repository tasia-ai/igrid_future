const webpack = require('webpack');

module.exports = function override(config, env) {
  // Add fallbacks for Node.js modules
  config.resolve.fallback = {
    ...config.resolve.fallback,
    "assert": require.resolve("assert/"),
    "buffer": require.resolve("buffer/"),
    "crypto": false,
    "process": require.resolve("process/browser"),
    "stream": require.resolve("stream-browserify"),
    "util": require.resolve("util/"),
  };
  
  // Add plugins
  config.plugins.push(
    new webpack.ProvidePlugin({
      process: ['process', 'default'],
      Buffer: ['buffer', 'Buffer'],
    })
  );
  
  return config;
};
