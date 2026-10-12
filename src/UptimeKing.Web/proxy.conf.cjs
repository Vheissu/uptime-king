const target = process.env.services__api__http__0 || process.env.API_URL || 'http://localhost:5080';
module.exports = {
  '/api': { target, secure: false },
  '/hubs': { target, secure: false, ws: true },
  '/health': { target, secure: false },
};
