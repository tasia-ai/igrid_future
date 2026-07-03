// This file is for advanced webpack configuration
// react-scripts will pick this up if we name it properly

const webpack = require('webpack');

module.exports = function override(config, env) {
  // Fallbacks for Node.js modules
  config.resolve.fallback = {
    ...config.resolve.fallback,
    "assert": false,
    "buffer": false,
    "crypto": false,
    "process": false,
    "stream": false,
    "util": false,
  };
  
  return config;
};
