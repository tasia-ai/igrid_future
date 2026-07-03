/* global module, require, process */
const webpack = require('webpack');

module.exports = function override(config, env) {
  // Add fallbacks for Node.js modules
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
